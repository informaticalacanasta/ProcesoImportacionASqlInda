using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace DbInda.Worker.Logging;

public sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly int _retainedDays;
    private readonly long _maxFileBytes;
    private readonly ConcurrentDictionary<string, DailyFileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly object _writeLock = new();
    private string? _openDate;
    private StreamWriter? _writer;
    private bool _writeFailed;
    private DateTimeOffset _writeFailedUntil;

    public DailyFileLoggerProvider(string directory, int retainedDays, long maxFileBytes = 20 * 1024 * 1024)
    {
        _directory = directory;
        _retainedDays = retainedDays < 1 ? 1 : retainedDays;
        _maxFileBytes = maxFileBytes < 1 ? 1 : maxFileBytes;
        try
        {
            Directory.CreateDirectory(_directory);
            PurgeExpired();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _writeFailed = true;
            _writeFailedUntil = DateTimeOffset.UtcNow.AddMinutes(1);
        }
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, static (name, provider) => new DailyFileLogger(name, provider), this);

    internal void Write(string line)
    {
        lock (_writeLock)
        {
            if (_writeFailed && DateTimeOffset.UtcNow < _writeFailedUntil)
                return;

            try
            {
                var date = DateTime.UtcNow.ToString("yyyyMMdd");
                var path = Path.Combine(_directory, $"worker-{date}.log");
                if (_writer is null || _openDate != date)
                    Open(path, date);
                else if (_writer.BaseStream.Length >= _maxFileBytes)
                {
                    _writer.Dispose();
                    _writer = null;
                    Rotate(path, date);
                    Open(path, date);
                }

                _writer!.WriteLine(line);
                _writeFailed = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _writeFailed = true;
                _writeFailedUntil = DateTimeOffset.UtcNow.AddMinutes(1);
                try
                {
                    _writer?.Dispose();
                }
                catch (IOException)
                {
                }

                _writer = null;
                System.Diagnostics.Debug.WriteLine("Fallo de log en fichero: " + ex.Message);
            }
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch (IOException)
            {
            }

            _writer = null;
        }
    }

    private void Open(string path, string date)
    {
        Directory.CreateDirectory(_directory);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
        _openDate = date;
        PurgeExpired();
    }

    private static void Rotate(string path, string date)
    {
        if (!File.Exists(path))
            return;
        for (var i = 1; i < 1000; i++)
        {
            var rotated = Path.Combine(Path.GetDirectoryName(path)!, $"worker-{date}-{i:000}.log");
            if (File.Exists(rotated))
                continue;
            File.Move(path, rotated);
            return;
        }
    }

    private void PurgeExpired()
    {
        var cutoff = DateTime.UtcNow.Date.AddDays(-_retainedDays);
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_directory, "worker-*.log").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length < 15 || !DateTime.TryParseExact(name[7..15], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var day))
                continue;
            if (day >= cutoff)
                continue;
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class DailyFileLogger(string category, DailyFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => JsonLogScope.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null)
                return;

            provider.Write(JsonLogLine.Format(DateTimeOffset.UtcNow, logLevel, category, eventId, message, exception, JsonLogScope.CurrentValues()));
        }
    }
}

public static class JsonLogLine
{
    private static readonly Regex Secret = new(@"(?i)(password|pwd)\s*=\s*[^;]*", RegexOptions.Compiled);

    public static string Format(
        DateTimeOffset timestampUtc,
        LogLevel level,
        string category,
        EventId eventId,
        string? message,
        Exception? exception,
        IReadOnlyDictionary<string, object?>? scope)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ts"] = timestampUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["level"] = level.ToString(),
            ["category"] = category,
            ["event"] = eventId.Name ?? eventId.Id.ToString(),
            ["message"] = Redact(message)
        };
        if (scope is { Count: > 0 })
            payload["scope"] = scope;
        if (exception is not null)
            payload["exception"] = Describe(exception);
        return JsonSerializer.Serialize(payload);
    }

    public static string Redact(string? message)
        => message is null ? "" : Secret.Replace(message, "$1=***");

    private static object Describe(Exception exception)
    {
        var sql = Find(exception);
        if (sql is null)
        {
            return new Dictionary<string, object?>
            {
                ["type"] = exception.GetType().Name,
                ["message"] = Redact(exception.Message)
            };
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "SqlException",
            ["message"] = Redact(sql.Message),
            ["number"] = sql.Number,
            ["state"] = sql.State,
            ["class"] = sql.Class,
            ["procedure"] = string.IsNullOrEmpty(sql.Procedure) ? null : sql.Procedure,
            ["line"] = sql.LineNumber
        };
    }

    private static SqlException? Find(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
                return sql;
        }

        return null;
    }
}

internal static class JsonLogScope
{
    private static readonly AsyncLocal<ScopeNode?> Current = new();

    public static IDisposable Push<TState>(TState state)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var pair in pairs)
                values[pair.Key] = pair.Value is string text ? JsonLogLine.Redact(text) : pair.Value;
        }
        else
        {
            values["scope"] = JsonLogLine.Redact(state?.ToString());
        }

        var node = new ScopeNode(Current.Value, values);
        Current.Value = node;
        return node;
    }

    public static IReadOnlyDictionary<string, object?>? CurrentValues()
    {
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var node = Current.Value; node is not null; node = node.Parent)
        {
            foreach (var pair in node.Values)
                merged.TryAdd(pair.Key, pair.Value);
        }

        return merged.Count == 0 ? null : merged;
    }

    private sealed class ScopeNode(ScopeNode? parent, Dictionary<string, object?> values) : IDisposable
    {
        public ScopeNode? Parent { get; } = parent;
        public Dictionary<string, object?> Values { get; } = values;

        public void Dispose()
        {
            if (ReferenceEquals(Current.Value, this))
                Current.Value = Parent;
        }
    }
}
