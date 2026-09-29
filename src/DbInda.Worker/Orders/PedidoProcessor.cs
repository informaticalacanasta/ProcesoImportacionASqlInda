using System.Globalization;
using DbInda.Worker.Configuration;
using DbInda.Worker.Files;
using DbInda.Worker.Inbound;
using DbInda.Worker.Parsing;
using DbInda.Worker.Processing;
using Microsoft.Extensions.Options;

namespace DbInda.Worker.Orders;

public sealed class PedidoProcessor
{
    private readonly OrderOptions _orders;
    private readonly FileReadinessChecker _readiness;
    private readonly PedidoTxtParser _parser;
    private readonly IPedidoRepository _repository;
    private readonly SqlRetryScheduler _retry;
    private readonly ILogger<PedidoProcessor> _logger;

    public PedidoProcessor(
        IOptions<OrderOptions> orders,
        IOptions<RetryOptions> retry,
        FileReadinessChecker readiness,
        PedidoTxtParser parser,
        IPedidoRepository repository,
        TimeProvider time,
        ILogger<PedidoProcessor> logger)
    {
        _orders = orders.Value;
        _readiness = readiness;
        _parser = parser;
        _repository = repository;
        _retry = new SqlRetryScheduler(retry, time);
        _logger = logger;
    }

    public async Task ProcessAsync(string path, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(path);
        if (!PedidoFileClassifier.IsImportCandidate(name))
            return;
        if (_retry.ShouldDefer(path, out _))
            return;

        var stable = await _readiness.WaitForStableObservationAsync(path, cancellationToken).ConfigureAwait(false);
        if (stable is null || !_readiness.Matches(path, stable.Value))
            return;

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Order file is not readable yet. File={File}", name);
            return;
        }

        if (!_readiness.Matches(path, stable.Value))
            return;

        var hash = Sha256FileHasher.ComputeHex(bytes);
        _logger.LogInformation("Order file detected. File={File}", name);
        _logger.LogInformation("Processing order file. File={File} Hash={Hash}", name, hash);

        PedidoReceptionRow? existing;
        try
        {
            existing = await _repository.FindByHashAsync(hash, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (PedidoSqlErrors.IsTransient(ex))
        {
            _retry.RegisterFailure(path);
            _logger.LogWarning("Temporary SQL error while importing order. File={File}. Will retry.", name);
            return;
        }

        if (existing is not null && IsFinished(existing.Estado))
        {
            _logger.LogInformation("Order file already processed. File={File} Hash={Hash}", name, hash);
            await ArchiveProcessedAsync(path, name, hash, existing, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (existing is not null && string.Equals(existing.Estado, PedidoReceptionStatuses.Error, StringComparison.Ordinal))
        {
            MoveToErrors(path, name, hash, fileDate: null, storeId: null);
            _retry.Clear(path);
            return;
        }

        var text = XmlTextDecoder.Decode(bytes).Text;
        var parsed = _parser.Parse(name, text);
        if (!parsed.Success || parsed.Document is null)
        {
            PedidoFileIdentity? identity = PedidoFileClassifier.TryMatch(name, out var matched) ? matched : null;
            await RejectAsync(path, name, hash, bytes.LongLength, parsed.Error ?? "Pedido no válido.", identity, cancellationToken).ConfigureAwait(false);
            return;
        }

        var document = parsed.Document;
        var directory = ProcessedDirectory(document.Header);
        string planned;
        try
        {
            planned = PedidoFileArchive.Plan(name, directory, hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "El pedido sigue en pendientes porque no se pudo preparar la carpeta de archivo. File={File}", name);
            return;
        }

        var saved = await _repository.SaveAsync(new PedidoSaveRequest
        {
            FileName = name,
            SourcePath = path,
            FinalPath = planned,
            Hash = hash,
            Size = bytes.LongLength,
            Header = document.Header,
            Lines = document.Lines,
            StoreFromName = document.Identity.StoreId,
            CashFromName = document.Identity.CashRegisterId
        }, cancellationToken).ConfigureAwait(false);

        if (saved.Outcome == PedidoSaveOutcome.Transient)
        {
            _retry.RegisterFailure(path);
            _logger.LogWarning("Temporary SQL error while importing order. File={File}. Will retry.", name);
            return;
        }

        if (saved.Outcome == PedidoSaveOutcome.Permanent)
        {
            await RejectAsync(path, name, hash, bytes.LongLength, saved.Error ?? "Error SQL permanente.", document.Identity, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (saved.Outcome == PedidoSaveOutcome.Duplicate)
        {
            _logger.LogInformation("Order file already processed. File={File} Hash={Hash}", name, hash);
            await ArchiveProcessedAsync(path, name, hash, existing: null, cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "Order imported. File={File} OrderId={OrderId} Store={Store} OrderNumber={OrderNumber} Lines={Lines}",
            name,
            saved.OrderId,
            document.Header.StoreId,
            document.Header.OrderNumber,
            document.Header.LineCount);
        var placed = PlaceProcessed(path, planned, hash, name);
        if (placed is not null && saved.ReceptionId is long receptionId && !string.Equals(placed, planned, StringComparison.OrdinalIgnoreCase))
            await _repository.TryUpdateFinalPathAsync(receptionId, placed, cancellationToken).ConfigureAwait(false);
        if (placed is not null)
            _retry.Clear(path);
    }

    private async Task ArchiveProcessedAsync(
        string path,
        string name,
        string hash,
        PedidoReceptionRow? existing,
        CancellationToken cancellationToken)
    {
        if (!PedidoFileClassifier.TryMatch(name, out var identity) || identity.OrderedAt is null)
        {
            _logger.LogWarning("Order file already processed but the name has no usable date. File={File}", name);
            return;
        }

        var directory = Path.Combine(
            Path.GetFullPath(_orders.Processed),
            identity.OrderedAt.Value.ToString("yyyy", CultureInfo.InvariantCulture),
            identity.OrderedAt.Value.ToString("MM", CultureInfo.InvariantCulture),
            identity.OrderedAt.Value.ToString("dd", CultureInfo.InvariantCulture),
            identity.StoreId.ToString(CultureInfo.InvariantCulture));
        string planned;
        try
        {
            planned = existing?.RutaFinal is { Length: > 0 } ruta && Directory.Exists(Path.GetDirectoryName(ruta))
                ? ruta
                : PedidoFileArchive.Plan(name, directory, hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "El pedido ya estaba importado y el archivo sigue en pendientes. File={File} Hash={Hash}", name, hash);
            return;
        }

        if (PlaceProcessed(path, planned, hash, name) is not null)
            _retry.Clear(path);
    }

    private string? PlaceProcessed(string source, string planned, string hash, string name)
    {
        try
        {
            return PedidoFileArchive.Place(source, planned, hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "El pedido ya estaba importado y el archivo sigue en pendientes. File={File} Hash={Hash}", name, hash);
            return null;
        }
    }

    private async Task RejectAsync(
        string path,
        string name,
        string hash,
        long size,
        string reason,
        PedidoFileIdentity? identity,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning("Invalid order file. File={File} Reason={Reason}", name, reason);
        var write = await _repository.TryInsertErrorAsync(
            name,
            path,
            hash,
            size,
            identity?.StoreId,
            identity?.CashRegisterId,
            identity?.OrderedAt?.Date,
            reason,
            cancellationToken).ConfigureAwait(false);
        if (write == PedidoErrorWrite.Transient)
        {
            _retry.RegisterFailure(path);
            _logger.LogWarning("Temporary SQL error while importing order. File={File}. Will retry.", name);
            return;
        }

        MoveToErrors(path, name, hash, identity?.OrderedAt, identity?.StoreId);
        _retry.Clear(path);
    }

    private void MoveToErrors(string path, string name, string hash, DateTime? fileDate, int? storeId)
    {
        var day = fileDate ?? DateTime.Now;
        var directory = Path.Combine(
            Path.GetFullPath(_orders.Errors),
            day.ToString("yyyy", CultureInfo.InvariantCulture),
            day.ToString("MM", CultureInfo.InvariantCulture),
            day.ToString("dd", CultureInfo.InvariantCulture));
        if (storeId is int store)
            directory = Path.Combine(directory, store.ToString(CultureInfo.InvariantCulture));
        try
        {
            var planned = PedidoFileArchive.Plan(name, directory, hash);
            PedidoFileArchive.Place(path, planned, hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Invalid order file could not be moved. File={File}", name);
        }
    }

    private string ProcessedDirectory(PedidoHeader header)
        => Path.Combine(
            Path.GetFullPath(_orders.Processed),
            header.OrderedAt.ToString("yyyy", CultureInfo.InvariantCulture),
            header.OrderedAt.ToString("MM", CultureInfo.InvariantCulture),
            header.OrderedAt.ToString("dd", CultureInfo.InvariantCulture),
            header.StoreId.ToString(CultureInfo.InvariantCulture));

    private static bool IsFinished(string estado)
        => string.Equals(estado, PedidoReceptionStatuses.Procesado, StringComparison.Ordinal)
           || string.Equals(estado, PedidoReceptionStatuses.Duplicado, StringComparison.Ordinal);
}
