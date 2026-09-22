using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DbInda.Worker.Configuration;
using DbInda.Worker.Inbound;
using Microsoft.Extensions.Options;

namespace DbInda.Worker.Files;

public sealed class ReceivedFileOrganizer
{
    private readonly string _input;
    private readonly string _inbox;
    private readonly string _organized;
    private readonly OrganizationOptions _options;
    private readonly FileReadinessChecker _readiness;
    private readonly IArchivedInvoiceLookup _invoices;
    private readonly ILogger<ReceivedFileOrganizer> _logger;
    private readonly OrganizationJournal _journal;
    private static readonly Regex Txt = new(@"^(?<prefix>[0-9]+)_(?<name>.+\.txt)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Marker = new(@"^bnd(?<prefix>[0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public ReceivedFileOrganizer(IOptions<PathsOptions> paths, IOptions<OrganizationOptions> options,
        FileReadinessChecker readiness, IArchivedInvoiceLookup invoices, ILogger<ReceivedFileOrganizer> logger)
    {
        _input = Path.GetFullPath(paths.Value.Input);
        _options = options.Value;
        _inbox = Path.GetFullPath(string.IsNullOrWhiteSpace(_options.Inbox) ? Path.Combine(_input, "inbox") : _options.Inbox);
        _organized = Path.GetFullPath(string.IsNullOrWhiteSpace(_options.Organized) ? Path.Combine(_input, "inboxOrganizado") : _options.Organized);
        _readiness = readiness;
        _invoices = invoices;
        _logger = logger;
        var comparer = FilePathComparer.ForIdentity;
        if (comparer.Equals(_input, _inbox) || comparer.Equals(_input, _organized) || comparer.Equals(_inbox, _organized)
            || _inbox.StartsWith(_organized + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || _organized.StartsWith(_inbox + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Input, inbox e inboxOrganizado deben ser carpetas independientes, sin anidar inbox e inboxOrganizado.");
        _journal = new OrganizationJournal(Path.Combine(_inbox, ".organizacion"));
    }

    public async Task ScanAsync(CancellationToken token)
    {
        if (!Directory.Exists(_input)) return;
        Directory.CreateDirectory(_inbox);
        Directory.CreateDirectory(_organized);
        // Released after each pass; another process must never mutate the same journal concurrently.
        using var ownership = new FileStream(Path.Combine(_inbox, ".organizador.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var entries = _journal.Load().ToList(); // A corrupt journal stops this pass, never silently discards history.
        foreach (var entry in entries.Where(e => e.State == "Planned"))
            await IsolateAsync(() => FinishMoveAsync(entry, token), entry.Source, token);

        // Stage TXT even before their marker arrives. No publication before a new marker.
        foreach (var path in Directory.EnumerateFiles(_input, "*", SearchOption.TopDirectoryOnly).ToArray())
        {
            var match = Txt.Match(Path.GetFileName(path));
            if (!match.Success || HasPending(entries, path)) continue;
            await IsolateAsync(async () =>
            {
                if (!await ReadyAsync(path, token)) return;
                var entry = Plan(path, "Txt", match.Groups["prefix"].Value, _inbox);
                _journal.Save(entry);
                entries.Add(entry);
                await FinishMoveAsync(entry, token);
            }, path, token);
        }

        foreach (var path in Directory.EnumerateFiles(_input, "*", SearchOption.TopDirectoryOnly).ToArray())
        {
            var match = Marker.Match(Path.GetFileName(path));
            if (!match.Success || HasPending(entries, path)) continue;
            await IsolateAsync(async () =>
            {
                if (!await ReadyAsync(path, token)) return;
                var prefix = match.Groups["prefix"].Value;
                // A still-uploading or inaccessible TXT holds its marker in the arrival directory.
                if (Directory.EnumerateFiles(_input).Any(p => Txt.Match(Path.GetFileName(p)) is { Success: true } m
                    && m.Groups["prefix"].Value == prefix)) return;
                var assigned = entries.Where(e => e.Kind == "Marker").SelectMany(e => e.Members).ToHashSet();
                var entry = Plan(path, "Marker", prefix, _inbox);
                entry.Members = entries.Where(e => e.Kind == "Txt" && e.Prefix == prefix
                    && e.State == "Staged" && !assigned.Contains(e.Id)).Select(e => e.Id).ToList();
                // Membership is durable BEFORE the marker leaves the arrival directory.
                _journal.Save(entry);
                entries.Add(entry);
                await FinishMoveAsync(entry, token);
            }, path, token);
        }

        var authorized = entries.Where(e => e.Kind == "Marker" && e.State == "Staged")
            .SelectMany(e => e.Members).ToHashSet();
        foreach (var entry in entries.Where(e => e.Kind == "Txt" && authorized.Contains(e.Id)
                     && e.State is "Staged" or "Publishing" or "Collision"))
            await IsolateAsync(() => PublishAsync(entry, token), entry.Source, token);

        foreach (var path in Directory.EnumerateFiles(_input, "*", SearchOption.TopDirectoryOnly)
                     .Where(p => Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            if (HasPending(entries, path)) continue;
            await IsolateAsync(async () =>
            {
                if (!await ReadyAsync(path, token)) return;
                var stem = Path.GetFileNameWithoutExtension(path);
                var a4 = stem.EndsWith("_a4_sin_firmar", StringComparison.OrdinalIgnoreCase);
                var xmlStem = a4 ? stem[..^"_a4_sin_firmar".Length] + "_sin_firmar" : stem;
                var destinations = await _invoices.FindAsync(Path.Combine(_input, xmlStem + ".xml"), token);
                if (destinations.Count != 1)
                {
                    if (destinations.Count > 1)
                        _logger.LogWarning("PDF con varias recepciones posibles; se conserva pendiente: {Path}", path);
                    return;
                }
                if (!File.Exists(destinations[0])) return;
                var archivedStem = Path.GetFileNameWithoutExtension(destinations[0]);
                // Keep the A4 label even when the XML acquired a collision suffix.
                var pdfName = a4 ? archivedStem.Replace("_sin_firmar", "_a4_sin_firmar", StringComparison.OrdinalIgnoreCase) + ".pdf"
                    : archivedStem + ".pdf";
                var entry = Plan(path, "Pdf", "", Path.GetDirectoryName(destinations[0])!, pdfName);
                _journal.Save(entry);
                entries.Add(entry);
                await FinishMoveAsync(entry, token);
            }, path, token);
        }
        RetireCompleted(entries);
    }

    private void RetireCompleted(List<OrganizationEntry> entries)
    {
        var completed = entries.Where(e => e.State == "Complete").Select(e => e.Id).ToHashSet();
        foreach (var marker in entries.Where(e => e.Kind == "Marker" && e.State == "Staged"))
        {
            if (!marker.Members.All(id => completed.Contains(id) || _journal.IsRetired(id))) continue;
            marker.State = "Complete";
            _journal.Save(marker);
        }
        foreach (var entry in entries.Where(e => e.State == "Complete" || e.Kind == "Pdf" && e.State == "Staged"))
            _journal.Retire(entry);
    }

    private static bool HasPending(List<OrganizationEntry> entries, string path) =>
        entries.Any(e => e.Source == path && e.State == "Planned");

    private OrganizationEntry Plan(string source, string kind, string prefix, string directory, string? name = null)
    {
        var entry = new OrganizationEntry { Source = source, Kind = kind, Prefix = prefix, Hash = Hash(source) };
        name ??= Path.GetFileName(source);
        var destination = Path.Combine(directory, name);
        if (File.Exists(destination))
        {
            destination = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(name)}_REPETIDO_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{entry.Id}{Path.GetExtension(name)}");
            _logger.LogWarning("Nombre repetido recibido posteriormente: {Source} -> {Destination}", source, destination);
        }
        entry.Destination = destination;
        return entry;
    }

    private async Task FinishMoveAsync(OrganizationEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (File.Exists(entry.Destination))
        {
            if (Hash(entry.Destination) != entry.Hash)
                throw new IOException($"Destino ocupado con otro contenido: {entry.Destination}");
            // Crash after rename: an existing source may be a NEW arrival. Never delete it.
        }
        else
        {
            if (!File.Exists(entry.Source)) throw new IOException($"No existe origen ni destino: {entry.Source}");
            if (!await ReadyAsync(entry.Source, token)) return;
            if (Hash(entry.Source) != entry.Hash) throw new IOException($"Origen cambiado; se conserva: {entry.Source}");
            File.Move(entry.Source, entry.Destination, overwrite: false);
        }
        entry.State = "Staged";
        _journal.Save(entry);
        _logger.LogInformation("Archivo organizado: {Source} -> {Destination}", entry.Source, entry.Destination);
    }

    private Task PublishAsync(OrganizationEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var original = Txt.Match(Path.GetFileName(entry.Source));
        var destination = Path.Combine(_organized, original.Groups["name"].Value);
        if (File.Exists(destination))
        {
            // Only a previously recorded publication may be reconciled by hash.
            if (entry.State == "Publishing" && entry.CopyDestination is not null && FilePathComparer.ForIdentity.Equals(Path.GetFullPath(entry.CopyDestination), destination) && Hash(destination) == entry.Hash)
            {
                entry.State = "Complete";
                _journal.Save(entry);
                return Task.CompletedTask;
            }
            if (entry.State != "Collision")
                _logger.LogWarning("Colisión TXT: no se sobrescribe {Destination}. Original conservado en {Source}", destination, entry.Destination);
            entry.State = "Collision";
            _journal.Save(entry);
            return Task.CompletedTask;
        }
        if (Hash(entry.Destination) != entry.Hash) throw new IOException($"TXT conservado modificado: {entry.Destination}");
        var temp = Path.Combine(_organized, "." + entry.Id + ".partial");
        // Private temporary path: safe to recreate after interruption. Readers never see a partial TXT.
        using (var source = File.OpenRead(entry.Destination))
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        if (Hash(temp) != entry.Hash) throw new IOException($"Copia TXT no verificada: {temp}");
        entry.CopyDestination = destination;
        entry.State = "Publishing";
        _journal.Save(entry);
        File.Move(temp, destination, overwrite: false);
        entry.State = "Complete";
        _journal.Save(entry);
        _logger.LogInformation("TXT publicado: {Source} -> {Destination}", entry.Destination, destination);
        return Task.CompletedTask;
    }

    private async Task<bool> ReadyAsync(string path, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.ReadinessTimeoutSeconds));
        return await _readiness.WaitUntilReadyAsync(path, timeout.Token);
    }

    private async Task IsolateAsync(Func<Task> action, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { await action(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogError(ex, "Organización pendiente; archivos conservados: {Path}", path); }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}