using DbInda.Worker.Configuration;
using DbInda.Worker.Files;
using DbInda.Worker.Inbound;
using Microsoft.Extensions.Options;

namespace DbInda.Worker.Orders;

/// <summary>
/// Copies a stable order from inbox into pedidos/pendientes without SQL.
/// The importer cannot see the final name until PREPARED is durable:
/// stabilize, hash, copy into .staging, verify, save PREPARED, publish, save MIRRORED.
/// A later crash recovers from the journal and the staging file. Inbox originals stay put.
/// </summary>
public sealed class PedidoFileMirror
{
    private readonly OrderOptions _orders;
    private readonly string _inbox;
    private readonly string _pending;
    private readonly string _processed;
    private readonly string _errors;
    private readonly string _staging;
    private readonly FileReadinessChecker _readiness;
    private readonly IFileStabilityProbe _probe;
    private readonly PedidoMirrorJournal _journal;
    private readonly TimeProvider _time;
    private readonly ILogger<PedidoFileMirror> _logger;
    private readonly SemaphoreSlim _publish = new(1, 1);

    public PedidoFileMirror(
        IOptions<OrderOptions> orders,
        IOptions<PathsOptions> paths,
        IOptions<OrganizationOptions> organization,
        FileReadinessChecker readiness,
        IFileStabilityProbe probe,
        PedidoMirrorJournal journal,
        TimeProvider time,
        ILogger<PedidoFileMirror> logger)
    {
        _orders = orders.Value;
        _inbox = PedidoLayout.Inbox(paths.Value, organization.Value);
        _pending = Path.GetFullPath(_orders.Pending);
        _processed = Path.GetFullPath(_orders.Processed);
        _errors = Path.GetFullPath(_orders.Errors);
        _staging = PedidoLayout.Staging(_orders);
        _readiness = readiness;
        _probe = probe;
        _journal = journal;
        _time = time;
        _logger = logger;
        if (FilePathComparer.ForIdentity.Equals(_pending, _inbox))
            throw new InvalidOperationException("Orders:Pending no puede ser la bandeja inbox.");
    }

    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_pending);
        Directory.CreateDirectory(_processed);
        Directory.CreateDirectory(_errors);
        Directory.CreateDirectory(_staging);
        await RecoverAsync(cancellationToken).ConfigureAwait(false);
        if (!Directory.Exists(_inbox))
        {
            _logger.LogDebug("La bandeja inbox aún no existe. Inbox={Inbox}", _inbox);
            return;
        }

        var files = Directory.EnumerateFiles(_inbox)
            .Where(PedidoFileClassifier.IsOrderFile)
            .ToList();
        await Parallel.ForEachAsync(files, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _orders.MaxConcurrency),
            CancellationToken = cancellationToken
        }, async (file, token) =>
        {
            try
            {
                await MirrorOneAsync(file, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo espejar el pedido. Se reintentará. File={File}", Path.GetFileName(file));
            }
        }).ConfigureAwait(false);
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_staging);
        Directory.CreateDirectory(_pending);
        foreach (var temp in Directory.EnumerateFiles(_staging, "*.tmp").ToList())
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "No se pudo borrar una copia temporal incompleta. File={File}", Path.GetFileName(temp));
            }
        }

        await AdoptOrphansAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in _journal.Prepared())
            await ResumeAsync(record, preferredSource: null, observation: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task MirrorOneAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = FilePathNormalizer.Normalize(path);
        if (!_probe.TryObserve(normalized, out var length, out var written))
            return;
        if (_journal.IsMirroredSource(normalized, length, written.Ticks))
            return;

        var stable = await _readiness.WaitForStableObservationAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (stable is null || !_readiness.Matches(normalized, stable.Value))
            return;
        if (_journal.IsMirroredSource(normalized, stable.Value.Length, stable.Value.LastWriteTimeUtc.Ticks))
            return;

        if (!PedidoFileClassifier.TryMatch(Path.GetFileName(normalized), out var identity))
            return;

        var hash = Sha256FileHasher.ComputeHex(normalized);
        var source = new PedidoMirrorSource
        {
            Path = normalized,
            Length = stable.Value.Length,
            LastWriteTimeUtcTicks = stable.Value.LastWriteTimeUtc.Ticks
        };

        await _publish.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = _journal.Find(hash);
            if (existing is not null && string.Equals(existing.State, PedidoMirrorStates.Mirrored, StringComparison.Ordinal))
            {
                AddSource(existing, source);
                _journal.Save(existing);
                _logger.LogInformation(
                    "Pedido ya espejado. Se registra el origen sin crear otra copia. File={File} Hash={Hash}",
                    Path.GetFileName(normalized),
                    hash);
                return;
            }

            if (existing is not null && string.Equals(existing.State, PedidoMirrorStates.Prepared, StringComparison.Ordinal))
            {
                AddSource(existing, source);
                _journal.Save(existing);
                await ResumeAsync(existing, normalized, source, cancellationToken).ConfigureAwait(false);
                return;
            }

            var finalName = identity.LogicalName;
            var pendingPath = Path.Combine(_pending, finalName);
            if (File.Exists(pendingPath))
            {
                var occupied = Sha256FileHasher.ComputeHex(pendingPath);
                if (string.Equals(occupied, hash, StringComparison.OrdinalIgnoreCase))
                {
                    var adopted = NewRecord(hash, finalName, StagingName(hash, finalName), source, PedidoMirrorStates.Prepared);
                    _journal.Save(adopted);
                    MarkMirrored(adopted);
                    return;
                }

                finalName = PedidoFileClassifier.WithHashSuffix(identity.LogicalName, hash);
                _logger.LogWarning(
                    "Pedido con el mismo nombre y distinto contenido. Se copia sin sobrescribir. File={File} Destino={Destino}",
                    Path.GetFileName(normalized),
                    finalName);
            }

            var stagingName = StagingName(hash, finalName);
            var record = NewRecord(hash, finalName, stagingName, source, PedidoMirrorStates.Prepared);
            await CopyVerifiedAsync(normalized, Path.Combine(_staging, stagingName), hash, cancellationToken).ConfigureAwait(false);
            _journal.Save(record);
            Publish(record);
            MarkMirrored(record);
        }
        finally
        {
            _publish.Release();
        }
    }

    private async Task AdoptOrphansAsync(CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(_staging).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".corrupto", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!TrySplitStagingName(name, out var hash, out var finalName))
            {
                Aside(file);
                continue;
            }

            string actual;
            try { actual = Sha256FileHasher.ComputeHex(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "No se pudo leer una copia en staging. File={File}", name);
                continue;
            }

            if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Copia de staging incompleta. Se descarta y el original de inbox se volverá a copiar. File={File}", name);
                File.Delete(file);
                continue;
            }

            if (_journal.Find(hash) is not null)
                continue;

            var record = NewRecord(hash, finalName, name, source: null, PedidoMirrorStates.Prepared);
            _journal.Save(record);
            Publish(record);
            MarkMirrored(record);
            _logger.LogInformation("Copia de staging huérfana publicada en pendientes. File={File} Hash={Hash}", finalName, hash);
        }
    }

    private async Task ResumeAsync(
        PedidoMirrorRecord record,
        string? preferredSource,
        PedidoMirrorSource? observation,
        CancellationToken cancellationToken)
    {
        var pendingPath = Path.Combine(_pending, record.FinalName);
        if (File.Exists(pendingPath) && string.Equals(Sha256FileHasher.ComputeHex(pendingPath), record.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            if (observation is not null)
                AddSource(record, observation);
            MarkMirrored(record);
            return;
        }

        var stagingPath = string.IsNullOrWhiteSpace(record.StagingName)
            ? ""
            : Path.Combine(_staging, record.StagingName);
        if (stagingPath.Length > 0 && File.Exists(stagingPath))
        {
            if (string.Equals(Sha256FileHasher.ComputeHex(stagingPath), record.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Publish(record);
                if (observation is not null)
                    AddSource(record, observation);
                MarkMirrored(record);
                return;
            }

            File.Delete(stagingPath);
        }

        if (FindArchived(record.FinalName, record.Sha256) is not null)
        {
            if (observation is not null)
                AddSource(record, observation);
            MarkMirrored(record);
            _logger.LogInformation(
                "El pedido preparado ya estaba archivado. No se vuelve a copiar. File={File} Hash={Hash}",
                record.FinalName,
                record.Sha256);
            return;
        }

        var source = preferredSource;
        if (source is null || !File.Exists(source))
            source = record.Sources.Select(item => item.Path).FirstOrDefault(File.Exists);
        if (source is null)
        {
            _logger.LogWarning(
                "Pedido preparado sin copia ni origen. Se conserva el registro hasta que el original vuelva a inbox. File={File} Hash={Hash}",
                record.FinalName,
                record.Sha256);
            return;
        }

        if (!string.Equals(Sha256FileHasher.ComputeHex(source), record.Sha256, StringComparison.OrdinalIgnoreCase))
            return;

        if (string.IsNullOrWhiteSpace(record.StagingName))
            record.StagingName = StagingName(record.Sha256, record.FinalName);
        await CopyVerifiedAsync(source, Path.Combine(_staging, record.StagingName), record.Sha256, cancellationToken).ConfigureAwait(false);
        _journal.Save(record);
        Publish(record);
        if (observation is not null)
            AddSource(record, observation);
        MarkMirrored(record);
    }

    private void Publish(PedidoMirrorRecord record)
    {
        var stagingPath = Path.Combine(_staging, record.StagingName);
        if (!File.Exists(stagingPath))
            throw new FileNotFoundException("No está la copia preparada del pedido.", stagingPath);

        var destination = Path.Combine(_pending, record.FinalName);
        if (File.Exists(destination))
        {
            if (SameHash(destination, record.Sha256))
            {
                File.Delete(stagingPath);
                return;
            }

            var renamed = PedidoFileClassifier.WithHashSuffix(record.FinalName, record.Sha256);
            if (string.Equals(renamed, record.FinalName, StringComparison.OrdinalIgnoreCase))
                renamed = Path.GetFileNameWithoutExtension(record.FinalName) + "_" + record.Sha256 + Path.GetExtension(record.FinalName);
            record.FinalName = renamed;
            _journal.Save(record);
            destination = Path.Combine(_pending, record.FinalName);
            if (File.Exists(destination) && SameHash(destination, record.Sha256))
            {
                File.Delete(stagingPath);
                return;
            }
        }

        Directory.CreateDirectory(_pending);
        File.Move(stagingPath, destination, overwrite: false);
    }

    private static bool SameHash(string path, string hash)
        => string.Equals(Sha256FileHasher.ComputeHex(path), hash, StringComparison.OrdinalIgnoreCase);

    private void MarkMirrored(PedidoMirrorRecord record)
    {
        record.State = PedidoMirrorStates.Mirrored;
        record.MirroredUtc ??= _time.GetUtcNow();
        _journal.Save(record);
    }

    private string? FindArchived(string finalName, string hash)
    {
        foreach (var root in new[] { _processed, _errors })
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var file in Directory.EnumerateFiles(root, finalName, SearchOption.AllDirectories))
            {
                try
                {
                    if (string.Equals(Sha256FileHasher.ComputeHex(file), hash, StringComparison.OrdinalIgnoreCase))
                        return file;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "No se pudo comprobar un pedido archivado. File={File}", file);
                }
            }
        }

        return null;
    }

    private static async Task CopyVerifiedAsync(string source, string destination, string hash, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".tmp";
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true))
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 131072, useAsync: true))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }

        File.Move(temp, destination, overwrite: true);
        var actual = Sha256FileHasher.ComputeHex(destination);
        if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destination);
            throw new IOException("La copia del pedido no coincide con el hash del origen.");
        }
    }

    private static PedidoMirrorRecord NewRecord(string hash, string finalName, string stagingName, PedidoMirrorSource? source, string state)
    {
        var record = new PedidoMirrorRecord
        {
            Sha256 = hash,
            State = state,
            FinalName = finalName,
            StagingName = stagingName,
            PreparedUtc = DateTimeOffset.UtcNow
        };
        if (source is not null)
            record.Sources.Add(source);
        return record;
    }

    private static void AddSource(PedidoMirrorRecord record, PedidoMirrorSource source)
    {
        record.Sources.RemoveAll(item => FilePathComparer.ForIdentity.Equals(item.Path, source.Path));
        record.Sources.Add(source);
    }

    private static string StagingName(string hash, string finalName) => hash + "__" + finalName;

    private static bool TrySplitStagingName(string name, out string hash, out string finalName)
    {
        hash = "";
        finalName = "";
        var separator = name.IndexOf("__", StringComparison.Ordinal);
        if (separator != 64)
            return false;
        hash = name[..64];
        finalName = name[(separator + 2)..];
        return hash.All(Uri.IsHexDigit) && PedidoFileClassifier.IsImportCandidate(finalName);
    }

    private void Aside(string file)
    {
        var destination = file + ".corrupto";
        try
        {
            File.Move(file, destination, overwrite: false);
            _logger.LogWarning("Copia de staging no reconocida, apartada. File={File}", Path.GetFileName(destination));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudo apartar una copia de staging. File={File}", Path.GetFileName(file));
        }
    }
}
