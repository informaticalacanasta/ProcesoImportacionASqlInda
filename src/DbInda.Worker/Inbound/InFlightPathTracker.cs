using System.Collections.Concurrent;

namespace DbInda.Worker.Inbound;

public sealed class InFlightPathTracker
{
    private readonly ConcurrentDictionary<string, byte> _paths = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _paths.Count;

    public bool TryClaim(string normalizedPath) => _paths.TryAdd(normalizedPath, 0);

    public void Release(string normalizedPath) => _paths.TryRemove(normalizedPath, out _);

    public bool Contains(string normalizedPath) => _paths.ContainsKey(normalizedPath);
}
