using DbInda.Worker.Files;
using DbInda.Worker.Inbound;
using DbInda.Worker.Persistence;

namespace DbInda.Worker.Processing;

public sealed class XmlArchiveReconciler
{
    private readonly SqlConnectionFactory _connections;
    private readonly ReceptionRepository _receptions;
    private readonly IXmlFileArchiver _archiver;
    private readonly ILogger<XmlArchiveReconciler> _logger;

    public XmlArchiveReconciler(
        SqlConnectionFactory connections,
        ReceptionRepository receptions,
        IXmlFileArchiver archiver,
        ILogger<XmlArchiveReconciler> logger)
    {
        _connections = connections;
        _receptions = receptions;
        _archiver = archiver;
        _logger = logger;
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);
        var rows = await _receptions.ListIncompleteArchivesAsync(connection, cancellationToken).ConfigureAwait(false);

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ReconcileOneAsync(row, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "Fallo reconciliando archivo de ID_RECEPCION {ReceptionId}. ESTADO_ARCHIVO={EstadoArchivo}.",
                    row.IdRecepcion,
                    row.EstadoArchivo);
            }
        }
    }

    private async Task ReconcileOneAsync(IncompleteArchiveRow row, CancellationToken cancellationToken)
    {
        var origin = FilePathNormalizer.Normalize(row.RutaOrigen);
        var originExists = _archiver.Exists(origin);
        var prevista = string.IsNullOrWhiteSpace(row.RutaDestinoPrevista)
            ? null
            : FilePathNormalizer.Normalize(row.RutaDestinoPrevista);
        var destExists = prevista is not null && _archiver.Exists(prevista);
        var destHashOk = destExists
                         && prevista is not null
                         && row.HashSha256 is { Length: > 0 } expected
                         && string.Equals(_archiver.TryComputeHash(prevista), expected, StringComparison.OrdinalIgnoreCase);

        var decision = ArchiveReconciliation.Decide(
            originExists,
            destExists,
            destHashOk,
            prevista is not null);

        switch (decision)
        {
            case ArchiveReconcileDecision.AllocateAndMove:
                await ArchiveFromOriginAsync(row, origin, cancellationToken).ConfigureAwait(false);
                break;

            case ArchiveReconcileDecision.MoveOriginToDestination:
                await MoveThenFinalizeAsync(row, origin, prevista!, cancellationToken).ConfigureAwait(false);
                break;

            case ArchiveReconcileDecision.FinalizeFromDestination:
                await FinalizeAsync(row.IdRecepcion, prevista!).ConfigureAwait(false);
                _logger.LogInformation(
                    "Reconciliación B: destino previsto ya contiene el XML. ID_RECEPCION {ReceptionId}. Destino {Destino}.",
                    row.IdRecepcion,
                    prevista);
                break;

            case ArchiveReconcileDecision.FinalizeKeepOrigin:
                await FinalizeAsync(row.IdRecepcion, prevista!).ConfigureAwait(false);
                _logger.LogInformation(
                    "Reconciliación C: origen y destino existen con el hash esperado. Se archiva la recepción {ReceptionId} en {Destino} y se deja el origen {Origen} como posible nueva llegada.",
                    row.IdRecepcion,
                    prevista,
                    origin);
                break;

            case ArchiveReconcileDecision.ReallocateAndMove:
                _logger.LogWarning(
                    "Reconciliación C': el destino previsto {Destino} existe con hash distinto. No se toca. ID_RECEPCION {ReceptionId}.",
                    prevista,
                    row.IdRecepcion);
                await ArchiveFromOriginAsync(row, origin, cancellationToken).ConfigureAwait(false);
                break;

            default:
                await MarkErrorAsync(row.IdRecepcion).ConfigureAwait(false);
                _logger.LogError(
                    "Reconciliación D: no hay origen ni destino previsto válido. ID_RECEPCION {ReceptionId}. Origen {Origen}. Destino {Destino}.",
                    row.IdRecepcion,
                    origin,
                    prevista);
                break;
        }
    }

    private async Task ArchiveFromOriginAsync(IncompleteArchiveRow row, string origin, CancellationToken cancellationToken)
    {
        var dest = _archiver.AllocateDestination(ToRequest(row, origin));
        await MarkArchivingAsync(row.IdRecepcion, dest).ConfigureAwait(false);
        await MoveThenFinalizeAsync(row, origin, dest, cancellationToken).ConfigureAwait(false);
    }

    private async Task MoveThenFinalizeAsync(
        IncompleteArchiveRow row,
        string origin,
        string destination,
        CancellationToken cancellationToken)
    {
        var hash = row.HashSha256 ?? "";
        try
        {
            await _archiver.MoveToExactAsync(origin, destination, hash, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex) when (_archiver.Exists(destination)
                                     && hash.Length > 0
                                     && string.Equals(_archiver.TryComputeHash(destination), hash, StringComparison.OrdinalIgnoreCase)
                                     && _archiver.Exists(origin))
        {
            _logger.LogInformation(
                ex,
                "Move concurrente: destino {Destino} ya tiene el hash de ID_RECEPCION {ReceptionId}. No se borra el origen {Origen}.",
                destination,
                row.IdRecepcion,
                origin);
            await FinalizeAsync(row.IdRecepcion, destination).ConfigureAwait(false);
            return;
        }

        await FinalizeAsync(row.IdRecepcion, destination).ConfigureAwait(false);
    }

    private ArchiveRequest ToRequest(IncompleteArchiveRow row, string origin)
        => new()
        {
            SourcePath = string.IsNullOrWhiteSpace(row.NombreFichero) ? origin : Path.Combine(Path.GetDirectoryName(origin) ?? origin, row.NombreFichero),
            HashSha256 = row.HashSha256 ?? "",
            Kind = ReceptionLifecycle.ArchiveKindFor(row.Estado),
            FolderDate = FolderDate(row),
            Tienda = row.TiendaFichero,
            ReceptionId = row.IdRecepcion
        };

    private static DateOnly FolderDate(IncompleteArchiveRow row)
    {
        if (row.FechaFichero is DateTime fechaFichero)
            return DateOnly.FromDateTime(fechaFichero);
        if (row.FechaProcesado is DateTime fechaProcesado)
            return DateOnly.FromDateTime(fechaProcesado);
        return DateOnly.FromDateTime(row.FechaRecepcion);
    }

    private async Task MarkArchivingAsync(long idRecepcion, string destination)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync();
        await _receptions.MarkArchivingAsync(connection, null, idRecepcion, destination, CancellationToken.None);
    }

    private async Task FinalizeAsync(long idRecepcion, string destination)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync();
        await _receptions.MarkArchivedAsync(connection, null, idRecepcion, destination, CancellationToken.None);
    }

    private async Task MarkErrorAsync(long idRecepcion)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync();
        await _receptions.MarkArchiveErrorAsync(connection, null, idRecepcion, CancellationToken.None);
    }
}
