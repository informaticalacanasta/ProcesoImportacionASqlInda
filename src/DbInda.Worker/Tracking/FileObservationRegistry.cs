using System.Collections.Concurrent;
using DbInda.Worker.Inbound;

namespace DbInda.Worker.Tracking;

public interface IFileArrivalClock
{
    void NoteObserved(string normalizedPath);
}

public interface IScanActivity
{
    void NoteScan(bool inputAccessible);
}

public sealed class FileObservationRegistry : IFileArrivalClock
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _firstSeen = new(FilePathComparer.ForIdentity);
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private bool _dirty;

    public FileObservationRegistry(string path, TimeProvider time)
    {
        _path = path;
        _time = time;
        Load();
    }

    public void NoteObserved(string normalizedPath)
    {
        if (_firstSeen.TryAdd(normalizedPath, _time.GetUtcNow()))
            _dirty = true;
    }

    public DateTimeOffset? OldestOf(IEnumerable<string> presentPaths)
    {
        DateTimeOffset? oldest = null;
        foreach (var path in presentPaths)
        {
            if (!_firstSeen.TryGetValue(path, out var seen))
            {
                seen = _time.GetUtcNow();
                if (_firstSeen.TryAdd(path, seen))
                    _dirty = true;
            }

            if (oldest is null || seen < oldest)
                oldest = seen;
        }

        return oldest;
    }

    public void ForgetExcept(IReadOnlySet<string> present)
    {
        foreach (var key in _firstSeen.Keys)
        {
            if (!present.Contains(key) && _firstSeen.TryRemove(key, out _))
                _dirty = true;
        }
    }

    public void PersistIfDirty()
    {
        if (!_dirty)
            return;
        lock (_sync)
        {
            if (!_dirty)
                return;
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                var temp = _path + ".tmp";
                var payload = _firstSeen.ToDictionary(pair => pair.Key, pair => pair.Value, FilePathComparer.ForIdentity);
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    System.Text.Json.JsonSerializer.Serialize(stream, payload);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temp, _path, overwrite: true);
                _dirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _dirty = true;
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            var payload = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(_path));
            if (payload is null)
                return;
            foreach (var pair in payload)
                _firstSeen.TryAdd(pair.Key, pair.Value);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            try
            {
                File.Move(_path, _path + ".corrupto", overwrite: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

public sealed class InboundActivity : IScanActivity
{
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private DateTimeOffset _lastProgressUtc;
    private DateTimeOffset? _lastSuccessUtc;
    private DateTimeOffset? _episodeStartUtc;
    private DateTimeOffset? _episodeProgressUtc;
    private DateTimeOffset? _sqlDownSinceUtc;
    private bool _sqlKnown;
    private bool _sqlReachable;

    public InboundActivity(TimeProvider time)
    {
        _time = time;
        _lastProgressUtc = time.GetUtcNow();
        ConflictWatermarkLocal = DateTime.Now;
    }

    public DateTimeOffset? LastScanUtc { get; private set; }
    public bool LastScanOk { get; private set; }
    public DateTimeOffset? LastAttemptUtc { get; private set; }
    public DateTimeOffset? LastSuccessUtc
    {
        get { lock (_gate) return _lastSuccessUtc; }
    }

    public DateTimeOffset LastProgressUtc
    {
        get { lock (_gate) return _lastProgressUtc; }
    }

    public DateTime ConflictWatermarkLocal { get; private set; }

    public void AdvanceConflictWatermark(DateTime local)
    {
        ConflictWatermarkLocal = local;
    }
    public DateTimeOffset? SqlDownSinceUtc => _sqlDownSinceUtc;

    public void NoteScan(bool accessible)
    {
        LastScanUtc = _time.GetUtcNow();
        LastScanOk = accessible;
    }

    public void NoteAttempt()
    {
        LastAttemptUtc = _time.GetUtcNow();
    }

    public void ObservePending(int count)
    {
        lock (_gate)
        {
            if (count <= 0)
            {
                _episodeStartUtc = null;
                _episodeProgressUtc = null;
                return;
            }

            _episodeStartUtc ??= _time.GetUtcNow();
        }
    }

    public bool IsPendingStalled(TimeSpan threshold)
    {
        lock (_gate)
        {
            if (_episodeStartUtc is null)
                return false;
            var basis = _episodeProgressUtc ?? _episodeStartUtc.Value;
            return _time.GetUtcNow() - basis >= threshold;
        }
    }

    public void NoteSuccess()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_lastSuccessUtc is null || now > _lastSuccessUtc)
                _lastSuccessUtc = now;
            NoteProgress(now);
        }
    }

    public void NoteProgress()
    {
        lock (_gate)
            NoteProgress(_time.GetUtcNow());
    }

    private void NoteProgress(DateTimeOffset now)
    {
        if (now > _lastProgressUtc)
            _lastProgressUtc = now;
        if (_episodeStartUtc is not null && (_episodeProgressUtc is null || now > _episodeProgressUtc))
            _episodeProgressUtc = now;
    }

    public bool ObserveSql(bool reachable, out bool recovered, out bool lost)
    {
        recovered = false;
        lost = false;
        if (!_sqlKnown)
        {
            _sqlKnown = true;
            _sqlReachable = reachable;
            if (!reachable)
            {
                _sqlDownSinceUtc = _time.GetUtcNow();
                lost = true;
            }

            return reachable;
        }

        if (reachable && !_sqlReachable)
            recovered = true;
        if (!reachable && _sqlReachable)
            lost = true;
        _sqlReachable = reachable;
        if (reachable)
            _sqlDownSinceUtc = null;
        else
            _sqlDownSinceUtc ??= _time.GetUtcNow();
        return reachable;
    }
}
