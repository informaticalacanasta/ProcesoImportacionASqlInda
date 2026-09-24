using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbInda.Worker.Tracking;

public sealed class TrackingOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private static readonly Regex EventFileName = new(
        @"^(ejecucion|intento|evento)-([0-9a-fA-F]{32})\.json$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _directory;
    private readonly int _maxFiles;
    private readonly long _maxBytes;
    private readonly ILogger<TrackingOutbox> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _saturationLogged;
    private bool _ioLogged;

    public TrackingOutbox(string directory, int maxFiles, long maxBytes, ILogger<TrackingOutbox> logger)
    {
        _directory = directory;
        _maxFiles = maxFiles;
        _maxBytes = maxBytes;
        _logger = logger;
    }

    public async Task<bool> SaveAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return SaveCore(envelope);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OutboxMetrics> MeasureAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var files = ListEventFiles();
            long bytes = 0;
            DateTimeOffset? oldest = null;
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                bytes += info.Length;
                var created = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero);
                if (oldest is null || created < oldest)
                    oldest = created;
            }

            return new OutboxMetrics(files.Count, oldest, _saturationLogged, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogIoOnce(ex);
            return new OutboxMetrics(0, null, true, 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> FlushAsync(ITrackingStore store, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = LoadValid();
            var sent = 0;
            foreach (var item in pending.OrderBy(item => item.Sequence).ThenBy(item => item.CreatedUtc).ThenBy(item => item.Id))
            {
                try
                {
                    await ApplyAsync(store, item, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (InvalidDataException ex)
                {
                    if (!string.IsNullOrEmpty(item.SourcePath))
                        Quarantine(item.SourcePath, ex);
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Reenvío de seguimiento detenido en {Id}. El fichero local se conserva.", item.Id);
                    break;
                }

                try
                {
                    File.Delete(item.SourcePath!);
                    sent++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogIoOnce(ex);
                    break;
                }
            }

            return sent;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool SaveCore(OutboxEnvelope envelope)
    {
        try
        {
            var pending = PendingDirectory();
            Directory.CreateDirectory(pending);
            var files = ListEventFiles();
            var bytes = files.Sum(file => new FileInfo(file).Length);
            var path = Path.Combine(pending, FileName(envelope));
            var replacing = File.Exists(path);
            if (!replacing && (files.Count >= _maxFiles || bytes >= _maxBytes))
            {
                if (!_saturationLogged)
                {
                    _saturationLogged = true;
                    _logger.LogError(
                        "La bandeja local de seguimiento está llena ({Files} ficheros, {Bytes} bytes). No se aceptan eventos nuevos. Los pendientes se conservan. Límite {MaxFiles}/{MaxBytes}.",
                        files.Count,
                        bytes,
                        _maxFiles,
                        _maxBytes);
                }

                return false;
            }

            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, envelope, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
            if (replacing || files.Count + 1 < _maxFiles)
                _saturationLogged = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogIoOnce(ex);
            return false;
        }
    }

    private List<OutboxEnvelope> LoadValid()
    {
        Directory.CreateDirectory(PendingDirectory());
        var items = new List<OutboxEnvelope>();
        foreach (var file in ListEventFiles())
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<OutboxEnvelope>(File.ReadAllText(file));
                if (envelope is null || envelope.Id == Guid.Empty || string.IsNullOrWhiteSpace(envelope.Kind) || !IsApplicable(envelope))
                    throw new InvalidDataException("Sobre de seguimiento incompleto o no aplicable.");
                envelope.SourcePath = file;
                items.Add(envelope);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                Quarantine(file, ex);
            }
        }

        return items;
    }

    private void Quarantine(string file, Exception ex)
    {
        try
        {
            var corrupt = Path.Combine(_directory, "corruptos");
            Directory.CreateDirectory(corrupt);
            var dest = Path.Combine(corrupt, Path.GetFileName(file));
            File.Move(file, dest, overwrite: true);
            _logger.LogWarning(ex, "Entrada local de seguimiento ilegible apartada en {Path}. El resto se conserva.", dest);
        }
        catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
        {
            LogIoOnce(moveEx);
        }
    }

    private string PendingDirectory() => Path.Combine(_directory, "pendientes");

    private List<string> ListEventFiles()
    {
        var files = new List<string>();
        Collect(files, _directory);
        Collect(files, PendingDirectory());
        return files;
    }

    private static void Collect(List<string> files, string directory)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (EventFileName.IsMatch(Path.GetFileName(file)))
                files.Add(file);
        }
    }

    private static bool IsApplicable(OutboxEnvelope envelope) => envelope.Kind switch
    {
        "ejecucion" => envelope.Execution is not null,
        "intento" => envelope.Attempt is not null,
        "evento" => envelope.Event is not null,
        _ => false
    };

    private void LogIoOnce(Exception ex)
    {
        if (_ioLogged)
            return;
        _ioLogged = true;
        _logger.LogError(ex, "No se puede escribir la bandeja local de seguimiento en {Directory}. Si el disco está lleno, el historial local no es ilimitado. La importación continúa.", _directory);
    }

    private static string FileName(OutboxEnvelope envelope)
        => $"{envelope.Kind}-{envelope.Id:N}.json";

    private static async Task ApplyAsync(ITrackingStore store, OutboxEnvelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Kind)
        {
            case "ejecucion" when envelope.Execution is not null:
                await store.UpsertExecutionAsync(envelope.Execution, cancellationToken).ConfigureAwait(false);
                break;
            case "intento" when envelope.Attempt is not null:
                await store.UpsertAttemptAsync(envelope.Attempt, cancellationToken).ConfigureAwait(false);
                break;
            case "evento" when envelope.Event is not null:
                await store.InsertEventAsync(envelope.Event, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidDataException($"Sobre de seguimiento no aplicable: {envelope.Kind}.");
        }
    }
}
