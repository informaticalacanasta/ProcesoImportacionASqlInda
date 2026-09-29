using System.Text.Json;
using System.Text.RegularExpressions;
using DbInda.Worker.Configuration;
using DbInda.Worker.Inbound;
using DbInda.Worker.Orders;
using Microsoft.Extensions.Options;

namespace DbInda.Worker.Files;

public sealed class InboxCleanup
{
    private readonly string _inbox;
    private readonly string _journalDirectory;
    private readonly string _lockPath;
    private readonly InboxCleanupOptions _options;
    private readonly InboxCleanupLedger _ledger;
    private readonly PedidoMirrorJournal _mirror;
    private readonly TimeProvider _time;
    private readonly ILogger<InboxCleanup> _logger;
    private static readonly Regex Txt = new(@"^(?<prefix>[0-9]+)_(?<name>.+\.txt)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Marker = new(@"^bnd(?<prefix>[0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex RepetidoId = new(@"_REPETIDO_\d{8}_\d{6}_(?<id>[0-9a-fA-F]{32})(?:\.[^.]+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex RepetidoSuffix = new(@"_REPETIDO_\d{8}_\d{6}_[0-9a-fA-F]{32}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public InboxCleanup(
        IOptions<PathsOptions> paths,
        IOptions<OrganizationOptions> organization,
        IOptions<InboxCleanupOptions> cleanup,
        PedidoMirrorJournal mirror,
        TimeProvider time,
        ILogger<InboxCleanup> logger)
    {
        var input = Path.GetFullPath(paths.Value.Input);
        var options = organization.Value;
        _inbox = Path.GetFullPath(string.IsNullOrWhiteSpace(options.Inbox) ? Path.Combine(input, "inbox") : options.Inbox);
        _journalDirectory = Path.Combine(_inbox, ".organizacion");
        _lockPath = Path.Combine(_inbox, ".organizador.lock");
        _options = cleanup.Value;
        _ledger = new InboxCleanupLedger(Path.Combine(_inbox, ".limpieza"));
        _mirror = mirror;
        _time = time;
        _logger = logger;
    }

    public Task ScanAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(_inbox))
            return Task.CompletedTask;

        var candidates = ListCandidates();
        if (candidates.Count == 0)
            return Task.CompletedTask;

        FileStream? ownership;
        try
        {
            ownership = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation("Limpieza de inbox aplazada: el organizador tiene el bloqueo.");
            return Task.CompletedTask;
        }

        using (ownership)
        {
            if (!TryReadActive(out var active))
            {
                _logger.LogWarning("Diario activo de organización ilegible. La limpieza no borra en esta pasada.");
                return Task.CompletedTask;
            }

            var history = ReadHistorial(out var historyUnreadable);
            foreach (var path in candidates)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    Examine(path, active, history, historyUnreadable, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "No se pudo comprobar el archivo de inbox. File={File}", Path.GetFileName(path));
                }
            }
        }

        return Task.CompletedTask;
    }

    private List<string> ListCandidates()
    {
        var candidates = new List<string>();
        foreach (var path in Directory.EnumerateFiles(_inbox, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (name.Length == 0 || name[0] == '.')
                continue;
            if (Classify(name) == InboxFileKind.Other)
                continue;
            candidates.Add(path);
        }
        return candidates;
    }

    private void Examine(
        string path,
        IReadOnlyList<OrganizationEntry> active,
        IReadOnlyList<OrganizationEntry> history,
        bool historyUnreadable,
        CancellationToken token)
    {
        if (!IsDirectInboxFile(path))
            return;

        var name = Path.GetFileName(path);
        var kind = Classify(name);
        if (kind == InboxFileKind.Other)
            return;

        if (active.Any(entry => SamePath(entry.Destination, path)))
        {
            foreach (var claim in active.Where(entry => SamePath(entry.Destination, path)))
                _ledger.Forget(claim.Id);
            return;
        }

        var hash = Sha256FileHasher.ComputeHex(path);
        if (!TryResolve(path, name, kind, hash, history, historyUnreadable, out var evidence, out var doubtful))
        {
            if (!doubtful && RepetidoId.Match(name) is { Success: true } failed)
                _ledger.Forget(failed.Groups["id"].Value);
            return;
        }

        if (active.Any(entry => IdEquals(entry.Id, evidence.Id)))
        {
            _ledger.Forget(evidence.Id);
            return;
        }

        if (kind == InboxFileKind.Marker && !MembersPublished(evidence, active, history))
        {
            _ledger.Forget(evidence.Id);
            return;
        }

        if (kind == InboxFileKind.Txt && !TxtMayBeDeleted(name, hash, evidence.Id))
            return;

        var now = _time.GetUtcNow();
        token.ThrowIfCancellationRequested();
        var since = _ledger.TryGet(evidence.Id, hash);
        if (since is null)
        {
            _ledger.Remember(evidence.Id, hash, now);
            return;
        }

        // Una fecha futura no autoriza el borrado. Se conserva y no se sustituye por este reloj ni por la fecha del archivo.
        if (since.Value > now)
            return;

        var age = now - since.Value;
        if (age < TimeSpan.FromHours(_options.RetentionHours))
            return;

        token.ThrowIfCancellationRequested();
        File.Delete(path);
        _ledger.Forget(evidence.Id);
        _logger.LogInformation("Inbox cleanup deleted completed file. File={File} Age={Age}", name, age);
    }

    private bool TryResolve(
        string path,
        string name,
        InboxFileKind kind,
        string hash,
        IReadOnlyList<OrganizationEntry> history,
        bool historyUnreadable,
        out OrganizationEntry evidence,
        out bool doubtful)
    {
        evidence = null!;
        doubtful = false;
        var expectedKind = kind == InboxFileKind.Marker ? "Marker" : "Txt";
        if (name.Contains("_REPETIDO_", StringComparison.OrdinalIgnoreCase))
        {
            var match = RepetidoId.Match(name);
            if (!match.Success)
            {
                doubtful = true;
                return false;
            }

            var id = match.Groups["id"].Value;
            var entry = history.FirstOrDefault(item => IdEquals(item.Id, id));
            if (entry is null)
            {
                doubtful = historyUnreadable || HistorialFileUnreadable(id);
                return false;
            }

            if (!IsProof(entry, path, hash, expectedKind))
                return false;
            evidence = entry;
            return true;
        }

        if (historyUnreadable)
        {
            doubtful = true;
            return false;
        }

        var matches = history.Where(entry => IsProof(entry, path, hash, expectedKind)).ToList();
        if (matches.Count != 1)
        {
            foreach (var match in matches)
                _ledger.Forget(match.Id);
            return false;
        }

        evidence = matches[0];
        return true;
    }

    private static bool MembersPublished(
        OrganizationEntry marker,
        IReadOnlyList<OrganizationEntry> active,
        IReadOnlyList<OrganizationEntry> history)
    {
        if (active.Any(entry => (entry.Members ?? []).Any(member => IdEquals(member, marker.Id))))
            return false;

        foreach (var memberId in marker.Members ?? [])
        {
            if (!InboxCleanupLedger.IsEntryId(memberId))
                return false;
            if (active.Any(entry => IdEquals(entry.Id, memberId)))
                return false;
            var member = history.FirstOrDefault(entry => IdEquals(entry.Id, memberId));
            if (member is null
                || !string.Equals(member.Kind, "Txt", StringComparison.Ordinal)
                || !string.Equals(member.State, "Complete", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(member.Hash)
                || !IdEquals(member.Id, memberId))
                return false;
        }

        return true;
    }

    private bool TxtMayBeDeleted(string name, string hash, string entryId)
    {
        if (PedidoFileClassifier.IsOrderFile(name))
        {
            if (_mirror.IsPersistentlyMirrored(hash))
                return true;
            _ledger.Forget(entryId);
            return false;
        }

        // Parece un PEDIDOS_TMPP, pero el clasificador estricto no lo reconoce. No sigue el camino de un TXT genérico.
        if (LooksLikeOrderFamily(name))
        {
            _ledger.Forget(entryId);
            return false;
        }

        return true;
    }

    private static bool LooksLikeOrderFamily(string fileName)
    {
        var name = RepetidoSuffix.Replace(Path.GetFileName(fileName), "");
        return name.Contains("PEDIDOS_TMPP", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProof(OrganizationEntry entry, string path, string hash, string expectedKind)
        => InboxCleanupLedger.IsEntryId(entry.Id)
           && string.Equals(entry.Kind, expectedKind, StringComparison.Ordinal)
           && string.Equals(entry.State, "Complete", StringComparison.Ordinal)
           && string.Equals(entry.Hash, hash, StringComparison.OrdinalIgnoreCase)
           && SamePath(entry.Destination, path);

    private bool TryReadActive(out List<OrganizationEntry> entries)
    {
        entries = [];
        if (!Directory.Exists(_journalDirectory))
            return true;
        foreach (var file in Directory.EnumerateFiles(_journalDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<OrganizationEntry>(File.ReadAllText(file));
                if (entry is null || !InboxCleanupLedger.IsEntryId(entry.Id))
                    return false;
                entries.Add(entry);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        return true;
    }

    private List<OrganizationEntry> ReadHistorial(out bool anyUnreadable)
    {
        anyUnreadable = false;
        var entries = new List<OrganizationEntry>();
        var root = Path.Combine(_journalDirectory, "historial");
        if (!Directory.Exists(root))
            return entries;
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<OrganizationEntry>(File.ReadAllText(file));
                if (entry is null || !InboxCleanupLedger.IsEntryId(entry.Id))
                {
                    anyUnreadable = true;
                    _logger.LogWarning("Historial de organización ilegible. Se conserva el archivo de inbox. File={File}", Path.GetFileName(file));
                    continue;
                }
                entries.Add(entry);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                anyUnreadable = true;
                _logger.LogWarning(ex, "Historial de organización ilegible. Se conserva el archivo de inbox. File={File}", Path.GetFileName(file));
            }
        }
        return entries;
    }

    private bool HistorialFileUnreadable(string id)
    {
        if (!InboxCleanupLedger.IsEntryId(id))
            return true;
        var path = Path.Combine(_journalDirectory, "historial", id[..2], id + ".json");
        if (!File.Exists(path))
            return false;
        try
        {
            var entry = JsonSerializer.Deserialize<OrganizationEntry>(File.ReadAllText(path));
            return entry is null || !InboxCleanupLedger.IsEntryId(entry.Id);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private bool IsDirectInboxFile(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }

        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return false;
        var parent = Path.GetDirectoryName(Path.GetFullPath(info.FullName));
        return parent is not null && FilePathComparer.ForIdentity.Equals(parent, _inbox);
    }

    private static InboxFileKind Classify(string fileName)
    {
        if (Marker.IsMatch(fileName))
            return InboxFileKind.Marker;
        var stripped = RepetidoSuffix.Replace(fileName, "");
        if (!string.Equals(stripped, fileName, StringComparison.OrdinalIgnoreCase) && Marker.IsMatch(stripped))
            return InboxFileKind.Marker;
        if (Txt.IsMatch(fileName))
            return InboxFileKind.Txt;
        return InboxFileKind.Other;
    }

    private static bool SamePath(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
            return false;
        try
        {
            return FilePathComparer.ForIdentity.Equals(Path.GetFullPath(left), Path.GetFullPath(right));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IdEquals(string? left, string? right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private enum InboxFileKind { Other, Txt, Marker }
}
