using DbInda.Worker.Configuration;
using Microsoft.Extensions.Options;

namespace DbInda.Worker.Inbound;

public sealed class FileReadinessChecker
{
    private readonly ProcessingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IFileStabilityProbe _probe;
    private readonly ILogger<FileReadinessChecker> _logger;

    public FileReadinessChecker(
        IOptions<ProcessingOptions> options,
        TimeProvider timeProvider,
        IFileStabilityProbe probe,
        ILogger<FileReadinessChecker> logger)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
        _probe = probe;
        _logger = logger;
    }

    public async Task<bool> WaitUntilReadyAsync(string path, CancellationToken cancellationToken)
        => await WaitForStableObservationAsync(path, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<FileStabilityObservation?> WaitForStableObservationAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await WaitUntilReadyCoreAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public bool Matches(string path, FileStabilityObservation observation)
        => _probe.TryObserve(path, out var length, out var lastWriteUtc)
           && length == observation.Length
           && lastWriteUtc == observation.LastWriteTimeUtc;

    private async Task<FileStabilityObservation?> WaitUntilReadyCoreAsync(string path, CancellationToken cancellationToken)
    {
        long? previousLength = null;
        DateTime? previousWriteUtc = null;
        var stableCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_probe.Exists(path))
            {
                _logger.LogDebug("El archivo ya no existe: {Path}", path);
                return null;
            }

            if (!_probe.TryObserve(path, out var length, out var lastWriteUtc))
            {
                _logger.LogDebug("Archivo aún no estable: {Path}. Motivo: bloqueado o en escritura.", path);
                previousLength = null;
                previousWriteUtc = null;
                stableCount = 0;
                await Task.Delay(Delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (previousLength == length && previousWriteUtc == lastWriteUtc)
            {
                stableCount++;
            }
            else
            {
                if (previousLength is not null)
                {
                    _logger.LogDebug(
                        "Archivo aún no estable: {Path}. Motivo: cambió el tamaño o LastWriteTimeUtc (tamaño {Length}, escritura {LastWriteUtc}).",
                        path,
                        length,
                        lastWriteUtc);
                }

                previousLength = length;
                previousWriteUtc = lastWriteUtc;
                stableCount = 1;
            }

            if (stableCount >= _options.StableChecks)
                return new FileStabilityObservation(length, lastWriteUtc);

            await Task.Delay(Delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private TimeSpan Delay => TimeSpan.FromMilliseconds(_options.StableCheckDelayMilliseconds);
}
