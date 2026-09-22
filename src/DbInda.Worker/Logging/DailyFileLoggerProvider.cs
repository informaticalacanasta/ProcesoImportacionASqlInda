using System.Collections.Concurrent;

namespace DbInda.Worker.Logging;

public sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly int _retainedDays;
    private readonly ConcurrentDictionary<string, DailyFileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly object _writeLock = new();
    private string? _openDate;
    private StreamWriter? _writer;

    public DailyFileLoggerProvider(string directory, int retainedDays)
    {
        _directory = directory;
        _retainedDays = retainedDays < 1 ? 1 : retainedDays;
        Directory.CreateDirectory(_directory);
        PurgeExpired();
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, static (name, provider) => new DailyFileLogger(name, provider), this);

    internal void Write(string line)
    {
        lock (_writeLock)
        {
            var date = DateTime.Now.ToString("yyyyMMdd");
            if (_writer is null || _openDate != date)
            {
                _writer?.Dispose();
                var path = Path.Combine(_directory, $"worker-{date}.log");
                _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true
                };
                _openDate = date;
                PurgeExpired();
            }

            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void PurgeExpired()
    {
        var cutoff = DateTime.Today.AddDays(-_retainedDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "worker-????????.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length < 15 || !DateTime.TryParseExact(name[7..], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var day))
                continue;
            if (day < cutoff)
                File.Delete(file);
        }
    }

    private sealed class DailyFileLogger(string category, DailyFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null)
                return;

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {logLevel} {category}: {message}";
            if (exception is not null)
                line += Environment.NewLine + exception;
            provider.Write(line);
        }
    }
}
