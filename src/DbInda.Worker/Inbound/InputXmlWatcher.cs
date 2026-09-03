namespace DbInda.Worker.Inbound;

public sealed class InputXmlWatcher : IDisposable
{
    private readonly ILogger<InputXmlWatcher> _logger;
    private FileSystemWatcher? _watcher;
    private Action<string>? _onXmlPath;

    public InputXmlWatcher(ILogger<InputXmlWatcher> logger)
    {
        _logger = logger;
    }

    public bool TryStart(string directory, Action<string> onXmlPath)
    {
        Stop();
        _onXmlPath = onXmlPath;

        if (!Directory.Exists(directory))
        {
            _logger.LogWarning(
                "FileSystemWatcher no iniciado: la carpeta de entrada no existe ({Directory}). El scanner periódico cubrirá el descubrimiento.",
                directory);
            return false;
        }

        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                Filter = "*.xml",
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024
            };
            watcher.Created += OnCreated;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            _logger.LogInformation("FileSystemWatcher iniciado en {Directory}", directory);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "No se pudo iniciar FileSystemWatcher en {Directory}. El scanner periódico cubrirá el descubrimiento.",
                directory);
            return false;
        }
    }

    public void Stop()
    {
        var watcher = Interlocked.Exchange(ref _watcher, null);
        if (watcher is null)
            return;

        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnCreated;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error al detener FileSystemWatcher.");
        }
    }

    public void Dispose() => Stop();

    private void OnCreated(object sender, FileSystemEventArgs e) => Notify(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e) => Notify(e.FullPath);

    private void Notify(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                return;
            _onXmlPath?.Invoke(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Excepción aislada en FileSystemWatcher para {Path}", path);
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(
            e.GetException(),
            "FileSystemWatcher perdió eventos. El scanner periódico redescubrirá los XML pendientes.");
    }
}
