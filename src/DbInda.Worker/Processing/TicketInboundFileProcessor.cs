using System.Text;
using DbInda.Worker.Files;
using DbInda.Worker.Inbound;
using DbInda.Worker.Models;
using DbInda.Worker.Parsing;
using DbInda.Worker.Persistence;
using DbInda.Worker.Validation;

namespace DbInda.Worker.Processing;

public sealed class TicketInboundFileProcessor : IInboundFileProcessor
{
    private readonly TicketDocumentReader _reader;
    private readonly TicketXsdValidator _xsdValidator;
    private readonly TicketImportProcessor _importer;
    private readonly IXmlFileArchiver _archiver;
    private readonly XmlArchiveReconciler _reconciler;
    private readonly SqlRetryScheduler _retries;
    private readonly ILogger<TicketInboundFileProcessor> _logger;

    public TicketInboundFileProcessor(
        TicketDocumentReader reader,
        TicketXsdValidator xsdValidator,
        TicketImportProcessor importer,
        IXmlFileArchiver archiver,
        XmlArchiveReconciler reconciler,
        SqlRetryScheduler retries,
        ILogger<TicketInboundFileProcessor> logger)
    {
        _reader = reader;
        _xsdValidator = xsdValidator;
        _importer = importer;
        _archiver = archiver;
        _reconciler = reconciler;
        _retries = retries;
        _logger = logger;
    }

    public async Task ProcessAsync(string fullPath, CancellationToken cancellationToken)
    {
        var normalized = FilePathNormalizer.Normalize(fullPath);

        try
        {
            await _reconciler.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && SqlAvailability.IsUnavailable(ex))
        {
            _logger.LogWarning(ex, "Reconciliación de archivo aplazada: SQL no disponible.");
        }

        if (!File.Exists(normalized))
        {
            _logger.LogInformation("El XML ya no está en Entrada: {Path}", normalized);
            _retries.Clear(normalized);
            return;
        }

        var bytes = await File.ReadAllBytesAsync(normalized, cancellationToken).ConfigureAwait(false);
        var hash = bytes.Length > 0 ? Sha256FileHasher.ComputeHex(bytes) : "";
        var xml = Encoding.UTF8.GetString(bytes);
        var fileName = Path.GetFileName(normalized);
        var parse = _reader.Read(xml, fileName);
        var xsd = _xsdValidator.Validate(xml);

        ImportResult result;
        try
        {
            result = await _importer.ImportAsync(
                new TicketImportCommand
                {
                    FileName = fileName,
                    OriginPath = normalized,
                    FileBytes = bytes,
                    Parse = parse,
                    Xsd = xsd
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (SqlAvailability.IsUnavailable(ex))
        {
            var delay = _retries.RegisterFailure(normalized);
            _logger.LogWarning(
                ex,
                "SQL temporalmente no disponible antes o durante la recepción. El XML permanece en Entrada: {Path}. Próximo retry en {Delay}.",
                normalized,
                delay);
            return;
        }

        if (result.SqlUnavailable || result.ReceptionId is null && result.Status == ReceptionStatuses.ErrorSql)
        {
            var delay = _retries.RegisterFailure(normalized);
            _logger.LogWarning(
                "SQL temporalmente no disponible; no hay TICKET_RECEPCION. XML en Entrada: {Path}. Próximo retry en {Delay}.",
                normalized,
                delay);
            return;
        }

        if (result.ReusedReception && !result.ArchiveOnly)
        {
            _logger.LogInformation(
                "Recepción reutilizada para retry. Archivo: {Path}. ID_RECEPCION: {ReceptionId}. Intento: {Attempt}.",
                normalized,
                result.ReceptionId,
                result.AttemptNumber);
        }
        else if (!result.ArchiveOnly)
        {
            _logger.LogInformation(
                "Recepción nueva. Archivo: {Path}. ID_RECEPCION: {ReceptionId}. Intento: {Attempt}.",
                normalized,
                result.ReceptionId,
                result.AttemptNumber);
        }

        if (result.ArchiveOnly)
        {
            _logger.LogInformation(
                "Archivado pendiente recuperado. Archivo: {Path}. ID_RECEPCION: {ReceptionId}. Estado: {Status}.",
                normalized,
                result.ReceptionId,
                result.Status);
        }

        LogImportOutcome(normalized, result);

        if (result.Status == ReceptionStatuses.ErrorSql)
        {
            var delay = _retries.RegisterFailure(normalized);
            _logger.LogWarning(
                "ERROR_SQL: el XML permanece en Entrada para retry. Archivo: {Path}. ID_RECEPCION: {ReceptionId}. Intento: {Attempt}. Próximo retry en {Delay}.",
                normalized,
                result.ReceptionId,
                result.AttemptNumber,
                delay);
            return;
        }

        _retries.Clear(normalized);

        if (result.ReceptionId is null)
            return;

        if (!ReceptionLifecycle.ShouldArchive(result.Status))
            return;

        if (result.ArchiveOnly && result.EstadoArchivo == ArchiveStatuses.Archivando)
        {
            _logger.LogInformation(
                "Archivado ARCHIVANDO a cargo del reconciliador. No se mueve el origen. Archivo: {Path}. ID_RECEPCION: {ReceptionId}.",
                normalized,
                result.ReceptionId);
            return;
        }

        try
        {
            var dest = await ArchiveDurableAsync(
                normalized, hash, parse, result.ReceptionId.Value, result.Status, cancellationToken).ConfigureAwait(false);
            var kind = ReceptionLifecycle.ArchiveKindFor(result.Status);
            _logger.LogInformation(
                kind == ArchiveKind.Error
                    ? "XML definitivo a Errores: {Path} -> {FinalPath}"
                    : "Movimiento a Procesados: {Path} -> {FinalPath}",
                normalized,
                dest);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Fallo de movimiento. La importación SQL no se deshace. Archivo: {Path}. ID_RECEPCION: {ReceptionId}.",
                normalized,
                result.ReceptionId);
        }
    }

    private async Task<string> ArchiveDurableAsync(
        string source,
        string hash,
        ParseResult parse,
        long receptionId,
        string importStatus,
        CancellationToken cancellationToken)
    {
        var request = new ArchiveRequest
        {
            SourcePath = source,
            HashSha256 = hash,
            Kind = ReceptionLifecycle.ArchiveKindFor(importStatus),
            FolderDate = parse.Ticket?.FechaExpedicion
                         ?? parse.FileName?.Fecha
                         ?? DateOnly.FromDateTime(DateTime.Now),
            Tienda = parse.Ticket?.Tienda ?? parse.FileName?.Tienda,
            ReceptionId = receptionId
        };

        var dest = _archiver.AllocateDestination(request);
        await _importer.BeginArchiveAsync(receptionId, dest, cancellationToken).ConfigureAwait(false);

        try
        {
            await _archiver.MoveToExactAsync(source, dest, hash, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) when (_archiver.Exists(dest))
        {
            dest = _archiver.AllocateDestination(request);
            await _importer.BeginArchiveAsync(receptionId, dest, cancellationToken).ConfigureAwait(false);
            await _archiver.MoveToExactAsync(source, dest, hash, cancellationToken).ConfigureAwait(false);
        }

        await CompleteArchiveWithRetryAsync(receptionId, dest, cancellationToken).ConfigureAwait(false);
        return dest;
    }

    private async Task CompleteArchiveWithRetryAsync(long receptionId, string rutaFinal, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var i = 0; i < 3; i++)
        {
            try
            {
                await _importer.CompleteArchiveAsync(receptionId, rutaFinal, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (SqlAvailability.IsUnavailable(ex) || ex is IOException)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
        }

        if (last is not null)
        {
            _logger.LogError(
                last,
                "El XML se movió a {RutaFinal} pero no se pudo marcar ARCHIVADO en ID_RECEPCION {ReceptionId}. Queda ARCHIVANDO para reconciliar.",
                rutaFinal,
                receptionId);
        }
    }

    private void LogImportOutcome(string path, ImportResult result)
    {
        switch (result.Status)
        {
            case ReceptionStatuses.Procesado:
            case ReceptionStatuses.ProcesadoConAdvertencias:
                _logger.LogInformation(
                    "Importación correcta. Archivo: {Path}. Estado: {Status}. Recepción: {ReceptionId}. Ticket: {TicketId}. Intento: {Attempt}.",
                    path, result.Status, result.ReceptionId, result.TicketId, result.AttemptNumber);
                break;
            case ReceptionStatuses.Duplicado:
                _logger.LogInformation(
                    "Duplicado. Archivo: {Path}. Recepción: {ReceptionId}. Ticket: {TicketId}.",
                    path, result.ReceptionId, result.TicketId);
                break;
            case ReceptionStatuses.ConflictoMismaFactura:
                _logger.LogInformation(
                    "Conflicto misma factura. Archivo: {Path}. Recepción: {ReceptionId}. Ticket: {TicketId}.",
                    path, result.ReceptionId, result.TicketId);
                break;
            case ReceptionStatuses.ErrorXml:
            case ReceptionStatuses.ErrorPermanente:
                _logger.LogWarning(
                    "XML no procesable definitivo. Archivo: {Path}. Recepción: {ReceptionId}. Estado: {Status}.",
                    path, result.ReceptionId, result.Status);
                break;
        }
    }
}
