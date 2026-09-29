using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbInda.Worker.Files;

public sealed class InboxCleanupMark
{
    public string EntryId { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTimeOffset? EligibleSinceUtc { get; set; }
}

// Own clock of the cleanup. It starts when an arrival is first proven finished,
// and it is not the organizer journal's file time.
public sealed class InboxCleanupLedger
{
    private static readonly Regex EntryId = new("^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly string _directory;

    public InboxCleanupLedger(string directory) => _directory = directory;

    public DateTimeOffset? TryGet(string entryId, string sha256)
    {
        if (!IsEntryId(entryId) || !File.Exists(PathFor(entryId)))
            return null;
        try
        {
            var mark = JsonSerializer.Deserialize<InboxCleanupMark>(File.ReadAllText(PathFor(entryId)));
            if (mark is null
                || !string.Equals(mark.EntryId, entryId, StringComparison.OrdinalIgnoreCase)
                || !IsMatchingHash(mark.Sha256, sha256)
                || !IsUsableEligibility(mark.EligibleSinceUtc))
                return null;
            return mark.EligibleSinceUtc;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Remember(string entryId, string sha256, DateTimeOffset eligibleSinceUtc)
    {
        if (!IsEntryId(entryId))
            throw new ArgumentException("El identificador de llegada no es válido.", nameof(entryId));
        Directory.CreateDirectory(_directory);
        var path = PathFor(entryId);
        var temp = path + ".tmp";
        var mark = new InboxCleanupMark
        {
            EntryId = entryId,
            Sha256 = sha256,
            EligibleSinceUtc = eligibleSinceUtc
        };
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, mark);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    public void Forget(string entryId)
    {
        if (!IsEntryId(entryId))
            return;
        var path = PathFor(entryId);
        if (File.Exists(path))
            File.Delete(path);
    }

    private string PathFor(string entryId) => Path.Combine(_directory, entryId + ".json");

    public static bool IsEntryId(string? entryId)
        => entryId is not null && EntryId.IsMatch(entryId);

    private static bool IsMatchingHash(string? stored, string actual)
        => stored is not null
           && stored.Length == 64
           && stored.All(Uri.IsHexDigit)
           && string.Equals(stored, actual, StringComparison.OrdinalIgnoreCase);

    // Ausente, nula o el valor por defecto de DateTimeOffset no son una espera empezada.
    private static bool IsUsableEligibility(DateTimeOffset? since)
        => since is { } value && value != default;
}
