using System.Text.Json;
using DbInda.Worker.Inbound;

namespace DbInda.Worker.Orders;

public sealed class PedidoMirrorJournal
{
    private readonly string _directory;
    private readonly ILogger<PedidoMirrorJournal> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, PedidoMirrorRecord> _byHash = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _hashByPath = new(FilePathComparer.ForIdentity);

    public PedidoMirrorJournal(string directory, ILogger<PedidoMirrorJournal> logger)
    {
        _directory = directory;
        _logger = logger;
        Load();
    }

    public bool IsMirroredSource(string path, long length, long lastWriteTimeUtcTicks)
    {
        lock (_sync)
        {
            if (!_hashByPath.TryGetValue(path, out var hash) || !_byHash.TryGetValue(hash, out var record))
                return false;
            if (!string.Equals(record.State, PedidoMirrorStates.Mirrored, StringComparison.Ordinal))
                return false;
            return record.Sources.Any(source =>
                FilePathComparer.ForIdentity.Equals(source.Path, path)
                && source.Length == length
                && source.LastWriteTimeUtcTicks == lastWriteTimeUtcTicks);
        }
    }

    public PedidoMirrorRecord? Find(string sha256)
    {
        lock (_sync)
            return _byHash.TryGetValue(sha256, out var record) ? record : null;
    }

    // Reads the last record flushed to disk. Find() can still show a state that Save() never persisted.
    public bool IsPersistentlyMirrored(string sha256)
    {
        if (!IsHash(sha256))
            return false;
        lock (_sync)
        {
            var path = Path.Combine(_directory, sha256 + ".json");
            if (!File.Exists(path))
                return false;
            try
            {
                var record = JsonSerializer.Deserialize<PedidoMirrorRecord>(File.ReadAllText(path));
                return record is not null
                    && string.Equals(record.Sha256, sha256, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(record.State, PedidoMirrorStates.Mirrored, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public IReadOnlyList<PedidoMirrorRecord> Prepared()
    {
        lock (_sync)
            return _byHash.Values
                .Where(record => string.Equals(record.State, PedidoMirrorStates.Prepared, StringComparison.Ordinal))
                .ToList();
    }

    public void Save(PedidoMirrorRecord record)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, record.Sha256 + ".json");
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, record);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            Remember(record);
        }
    }

    private void Load()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(_directory);
            foreach (var file in Directory.EnumerateFiles(_directory).ToList())
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".corrupto", StringComparison.OrdinalIgnoreCase))
                {
                    if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        Quarantine(file);
                    continue;
                }

                if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var record = JsonSerializer.Deserialize<PedidoMirrorRecord>(File.ReadAllText(file));
                    if (record is null
                        || !IsHash(record.Sha256)
                        || (record.State != PedidoMirrorStates.Prepared && record.State != PedidoMirrorStates.Mirrored)
                        || string.IsNullOrWhiteSpace(record.FinalName))
                    {
                        Quarantine(file);
                        continue;
                    }

                    Remember(record);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Diario de espejo ilegible. Se aparta y el pedido se volverá a reconocer desde inbox. File={File}", name);
                    Quarantine(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "No se pudo leer el diario de espejo. File={File}", name);
                    throw;
                }
            }
        }
    }

    private void Remember(PedidoMirrorRecord record)
    {
        _byHash[record.Sha256] = record;
        foreach (var source in record.Sources)
        {
            if (!string.IsNullOrWhiteSpace(source.Path))
                _hashByPath[source.Path] = record.Sha256;
        }
    }

    private void Quarantine(string file)
    {
        var destination = file + ".corrupto";
        if (File.Exists(destination))
            destination = file + "." + Guid.NewGuid().ToString("N") + ".corrupto";
        try
        {
            File.Move(file, destination, overwrite: false);
            _logger.LogWarning("Registro de espejo apartado. File={File}", Path.GetFileName(destination));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudo apartar el registro de espejo. File={File}", Path.GetFileName(file));
        }
    }

    private static bool IsHash(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);
}
