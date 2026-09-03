namespace DbInda.Worker.Inbound;

public sealed class InputDirectoryScanner
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InputDirectoryScanner> _logger;

    public InputDirectoryScanner(TimeProvider timeProvider, ILogger<InputDirectoryScanner> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task RunAsync(
        string directory,
        TimeSpan interval,
        Action<string> onDiscovered,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeScan = null)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (beforeScan is not null)
                await beforeScan(cancellationToken).ConfigureAwait(false);

            ScanOnce(directory, onDiscovered);
            await Task.Delay(interval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    public void ScanOnce(string directory, Action<string> onDiscovered)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                _logger.LogWarning("La carpeta de entrada no existe: {Directory}", directory);
                return;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.xml", SearchOption.TopDirectoryOnly))
                onDiscovered(file);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error al escanear la carpeta de entrada {Directory}", directory);
        }
    }
}
