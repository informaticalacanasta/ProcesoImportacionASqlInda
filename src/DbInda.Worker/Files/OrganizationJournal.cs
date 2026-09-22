using System.Text.Json;

namespace DbInda.Worker.Files;

public sealed class OrganizationEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Hash { get; set; } = "";
    public string State { get; set; } = "Planned";
    public string? CopyDestination { get; set; }
    public List<string> Members { get; set; } = [];
}

// One small journal per arrival. Completed entries remain as durable evidence.
// Only one organizer process may own an inbox (enforced by an exclusive lock).
public sealed class OrganizationJournal(string directory)
{
    private readonly object _sync = new();

    public IEnumerable<OrganizationEntry> Load()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(directory);
            var entries = new List<OrganizationEntry>();
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
                entries.Add(JsonSerializer.Deserialize<OrganizationEntry>(File.ReadAllText(file))
                    ?? throw new InvalidDataException($"Registro de organización inválido: {file}"));
            return entries;
        }
    }

    private string RetiredPath(string id) => Path.Combine(directory, "historial", id[..2], id + ".json");

    public bool IsRetired(string id)
    {
        lock (_sync)
            return File.Exists(RetiredPath(id));
    }

    public void Retire(OrganizationEntry entry)
    {
        lock (_sync)
        {
            var destination = RetiredPath(entry.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(Path.Combine(directory, entry.Id + ".json"), destination, overwrite: false);
        }
    }

    public void Save(OrganizationEntry entry)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, entry.Id + ".json");
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, entry);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
    }
}