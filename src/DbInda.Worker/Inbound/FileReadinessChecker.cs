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
    {
        try
        {
            return await WaitUntilReadyCoreAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> WaitUntilReadyCoreAsync(string path, CancellationToken cancellationToken)
    {
        long? previousLength = null;
        DateTime? previousWriteUtc = null;
        var stableCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_probe.Exists(path))
            {
                _logger.LogInformation("El archivo XML ya no existe: {Path}", path);
                return false;
            }

            if (!_probe.TryObserve(path, out var length, out var lastWriteUtc))
            {
                _logger.LogInformation("Archivo XML aún no estable: {Path}. Motivo: bloqueado o en escritura.", path);
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
                    _logger.LogInformation(
                        "Archivo XML aún no estable: {Path}. Motivo: cambió el tamaño o LastWriteTimeUtc (tamaño {Length}, escritura {LastWriteUtc}).",
                        path,
                        length,
                        lastWriteUtc);
                }

                previousLength = length;
                previousWriteUtc = lastWriteUtc;
                stableCount = 1;
            }

            if (stableCount >= _options.StableChecks)
                return true;

            await Task.Delay(Delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private TimeSpan Delay => TimeSpan.FromMilliseconds(_options.StableCheckDelayMilliseconds);
}
