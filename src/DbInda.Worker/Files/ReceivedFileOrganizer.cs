using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using DbInda.Worker.Configuration;
using DbInda.Worker.Inbound;
using Microsoft.Extensions.Options;

namespace DbInda.Worker.Files;

public sealed class ReceivedFileOrganizer
{
    private readonly string _input;
    private readonly string _inbox;
    private readonly string _organized;
    private readonly string _logs;
    private readonly OrganizationOptions _options;
    private readonly FileReadinessChecker _readiness;
    private readonly IArchivedInvoiceLookup _invoices;
    private readonly ILogger<ReceivedFileOrganizer> _logger;
    private readonly OrganizationJournal _journal;
    private readonly object _sync = new();
    private static readonly Regex Txt = new(@"^(?<prefix>[0-9]+)_(?<name>.+\.txt)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Marker = new(@"^bnd(?<prefix>[0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public ReceivedFileOrganizer(IOptions<PathsOptions> paths, IOptions<OrganizationOptions> options,
        FileReadinessChecker readiness, IArchivedInvoiceLookup invoices, ILogger<ReceivedFileOrganizer> logger)
    {
        _input = Path.GetFullPath(paths.Value.Input);
        _options = options.Value;
        _inbox = Path.GetFullPath(string.IsNullOrWhiteSpace(_options.Inbox) ? Path.Combine(_input, "inbox") : _options.Inbox);
        _organized = Path.GetFullPath(string.IsNullOrWhiteSpace(_options.Organized) ? Path.Combine(_input, "inboxOrganizado") : _options.Organized);
        _logs = Path.GetFullPath(string.IsNullOrWhiteSpace(_options.Logs) ? Path.Combine(_input, "logs") : _options.Logs);
        _readiness = readiness;
        _invoices = invoices;
        _logger = logger;
        var comparer = FilePathComparer.ForIdentity;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (comparer.Equals(_input, _inbox) || comparer.Equals(_input, _organized) || comparer.Equals(_inbox, _organized)
            || comparer.Equals(_logs, _input) || comparer.Equals(_logs, _inbox) || comparer.Equals(_logs, _organized)
            || Nested(_inbox, _organized, comparison) || Nested(_organized, _inbox, comparison)
            || Nested(_logs, _inbox, comparison) || Nested(_logs, _organized, comparison)
            || Nested(_inbox, _logs, comparison) || Nested(_organized, _logs, comparison))
            throw new ArgumentException("Input, inbox, inboxOrganizado y logs deben ser carpetas independientes, sin anidar inbox, inboxOrganizado ni logs entre sí.");
        _journal = new OrganizationJournal(Path.Combine(_inbox, ".organizacion"));
    }

    public async Task ScanAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(_input)) return;
        var clock = Stopwatch.StartNew();
        var stats = new OrganizationPass();
        Directory.CreateDirectory(_inbox);
        Directory.CreateDirectory(_organized);
        Directory.CreateDirectory(_logs);
        // Released after each pass; another process must never mutate the same journal concurrently.
        using var ownership = new FileStream(Path.Combine(_inbox, ".organizador.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var entries = _journal.Load().ToList();
        await RunBoundedAsync(entries.Where(e => e.State == "Planned").Select(entry => (Func<Task>)(() =>
            IsolateAsync(() => FinishMoveAsync(entry, known: null, stats, token), entry.Source, token))).ToList(), stats, token);

        var paths = Directory.EnumerateFiles(_input, "*", SearchOption.TopDirectoryOnly).ToArray();
        stats.Examined = paths.Length;
        var txt = new List<string>();
        var markers = new List<string>();
        var pdfs = new List<string>();
        var logs = new List<string>();
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (Txt.IsMatch(name)) txt.Add(path);
            else if (Marker.IsMatch(name)) markers.Add(path);
            else if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) pdfs.Add(path);
            else if (Path.GetExtension(path).Equals(".log", StringComparison.OrdinalIgnoreCase)) logs.Add(path);
        }

        var pdfQueries = new ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<string>>>>(StringComparer.Ordinal);
        // TXT, PDF and logs share one bounded pass so a TXT burst cannot occupy every worker first.
        await RunBoundedAsync(Interleave(
            txt.Select(path => (Func<Task>)(() => StageTxtAsync(path, entries, stats, token))),
            pdfs.Select(path => (Func<Task>)(() => StagePdfAsync(path, entries, pdfQueries, stats, token))),
            logs.Select(path => (Func<Task>)(() => StageLogAsync(path, entries, stats, token)))), stats, token);
        // Markers run after TXT attempts of this pass, so a file still arriving stays in its batch.
        await RunBoundedAsync(markers.Select(path => (Func<Task>)(() => StageMarkerAsync(path, entries, stats, token))).ToList(), stats, token);

        List<OrganizationEntry> publish;
        lock (_sync)
        {
            var authorized = entries.Where(e => e.Kind == "Marker" && e.State == "Staged")
                .SelectMany(e => e.Members).ToHashSet();
            publish = entries.Where(e => e.Kind == "Txt" && authorized.Contains(e.Id)
                         && e.State is "Staged" or "Publishing" or "Collision").ToList();
        }
        await RunBoundedAsync(publish.Select(entry => (Func<Task>)(() =>
            IsolateAsync(() => PublishAsync(entry, stats, token), entry.Source, token))).ToList(), stats, token);
        lock (_sync)
            RetireCompleted(entries);
        LogPass(clock.ElapsedMilliseconds, stats);
    }

    private void LogPass(long elapsedMs, OrganizationPass stats)
    {
        if (stats.Moved + stats.Published + stats.Deferred + stats.Collisions + stats.PdfPending + stats.PdfAmbiguous + stats.PdfQueries == 0)
        {
            _logger.LogDebug(
                "Pasada de organización en {ElapsedMs} ms sin movimientos. Examinados {Examined}. Concurrencia máxima {Peak}.",
                elapsedMs, stats.Examined, stats.Peak);
            return;
        }
        _logger.LogInformation(
            "Pasada de organización en {ElapsedMs} ms. Examinados {Examined}. Trasladados {Moved}. Publicados {Published}. Aplazados {Deferred}. Colisiones {Collisions}. PDF pendientes {PdfPending}. PDF ambiguos {PdfAmbiguous}. Consultas PDF {PdfQueries}. Concurrencia máxima {Peak}.",
            elapsedMs, stats.Examined, stats.Moved, stats.Published, stats.Deferred, stats.Collisions,
            stats.PdfPending, stats.PdfAmbiguous, stats.PdfQueries, stats.Peak);
    }

    private async Task StageTxtAsync(string path, List<OrganizationEntry> entries, OrganizationPass stats, CancellationToken token)
    {
        var match = Txt.Match(Path.GetFileName(path));
        lock (_sync)
        {
            if (HasPending(entries, path)) return;
        }
        await IsolateAsync(async () =>
        {
            var observed = await ObserveAsync(path, stats, token);
            if (observed is null || !StillSame(path, observed.Value, stats)) return;
            var hash = Hash(path);
            OrganizationEntry entry;
            lock (_sync)
            {
                if (HasPending(entries, path)) return;
                entry = Plan(path, "Txt", match.Groups["prefix"].Value, _inbox, hash: hash);
                _journal.Save(entry);
                entries.Add(entry);
            }
            await FinishMoveAsync(entry, observed, stats, token);
        }, path, token);
    }

    private async Task StageMarkerAsync(string path, List<OrganizationEntry> entries, OrganizationPass stats, CancellationToken token)
    {
        var match = Marker.Match(Path.GetFileName(path));
        lock (_sync)
        {
            if (HasPending(entries, path)) return;
        }
        await IsolateAsync(async () =>
        {
            var observed = await ObserveAsync(path, stats, token);
            if (observed is null || !StillSame(path, observed.Value, stats)) return;
            var prefix = match.Groups["prefix"].Value;
            if (Directory.EnumerateFiles(_input).Any(p => Txt.Match(Path.GetFileName(p)) is { Success: true } pending
                && pending.Groups["prefix"].Value == prefix))
                return;
            var hash = Hash(path);
            OrganizationEntry entry;
            lock (_sync)
            {
                if (HasPending(entries, path)) return;
                var assigned = entries.Where(e => e.Kind == "Marker").SelectMany(e => e.Members).ToHashSet();
                entry = Plan(path, "Marker", prefix, _inbox, hash: hash);
                entry.Members = entries.Where(e => e.Kind == "Txt" && e.Prefix == prefix
                    && e.State == "Staged" && !assigned.Contains(e.Id)).Select(e => e.Id).ToList();
                // Membership is durable BEFORE the marker leaves the arrival directory.
                _journal.Save(entry);
                entries.Add(entry);
            }
            await FinishMoveAsync(entry, observed, stats, token);
        }, path, token);
    }

    private async Task StagePdfAsync(string path, List<OrganizationEntry> entries,
        ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<string>>>> queries, OrganizationPass stats, CancellationToken token)
    {
        lock (_sync)
        {
            if (HasPending(entries, path)) return;
        }
        await IsolateAsync(async () =>
        {
            var observed = await ObserveAsync(path, stats, token);
            if (observed is null || !StillSame(path, observed.Value, stats)) return;
            var stem = Path.GetFileNameWithoutExtension(path);
            var a4 = stem.EndsWith("_a4_sin_firmar", StringComparison.OrdinalIgnoreCase);
            var xmlStem = a4 ? stem[..^"_a4_sin_firmar".Length] + "_sin_firmar" : stem;
            var origin = Path.Combine(_input, xmlStem + ".xml");
            IReadOnlyList<string> destinations;
            try
            {
                destinations = await queries.GetOrAdd(origin, key => new Lazy<Task<IReadOnlyList<string>>>(() =>
                {
                    Interlocked.Increment(ref stats.PdfQueries);
                    return _invoices.FindAsync(key, token);
                })).Value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref stats.PdfPending);
                _logger.LogDebug(ex, "Consulta PDF aplazada: {Path}", path);
                return;
            }
            if (destinations.Count != 1)
            {
                if (destinations.Count > 1)
                {
                    Interlocked.Increment(ref stats.PdfAmbiguous);
                    _logger.LogWarning("PDF con varias recepciones posibles; se conserva pendiente: {Path}", path);
                }
                else
                    Interlocked.Increment(ref stats.PdfPending);
                return;
            }
            if (!File.Exists(destinations[0]))
            {
                Interlocked.Increment(ref stats.PdfPending);
                return;
            }
            var archivedStem = Path.GetFileNameWithoutExtension(destinations[0]);
            var pdfName = a4 ? archivedStem.Replace("_sin_firmar", "_a4_sin_firmar", StringComparison.OrdinalIgnoreCase) + ".pdf"
                : archivedStem + ".pdf";
            var directory = Path.GetDirectoryName(destinations[0])!;
            var hash = Hash(path);
            OrganizationEntry entry;
            lock (_sync)
            {
                if (HasPending(entries, path)) return;
                entry = Plan(path, "Pdf", "", directory, pdfName, hash);
                _journal.Save(entry);
                entries.Add(entry);
            }
            await FinishMoveAsync(entry, observed, stats, token);
        }, path, token);
    }

    private async Task StageLogAsync(string path, List<OrganizationEntry> entries, OrganizationPass stats, CancellationToken token)
    {
        lock (_sync)
        {
            if (HasPending(entries, path)) return;
        }
        await IsolateAsync(async () =>
        {
            var observed = await ObserveAsync(path, stats, token);
            if (observed is null || !StillSame(path, observed.Value, stats)) return;
            var hash = Hash(path);
            OrganizationEntry entry;
            lock (_sync)
            {
                if (HasPending(entries, path)) return;
                entry = Plan(path, "Log", "", _logs, hash: hash);
                _journal.Save(entry);
                entries.Add(entry);
            }
            await FinishMoveAsync(entry, observed, stats, token);
        }, path, token);
    }

    private async Task RunBoundedAsync(IReadOnlyList<Func<Task>> work, OrganizationPass stats, CancellationToken token)
    {
        if (work.Count == 0) return;
        var capacity = Math.Max(1, _options.MaxConcurrency);
        var channel = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true
        });
        var workers = new Task[Math.Min(capacity, work.Count)];
        for (var i = 0; i < workers.Length; i++)
            workers[i] = ConsumeAsync(channel.Reader, stats);

        Exception? producerError = null;
        try
        {
            foreach (var item in work)
                await channel.Writer.WriteAsync(item, token);
        }
        catch (Exception ex)
        {
            producerError = ex;
        }
        channel.Writer.TryComplete();
        Exception? workerError = null;
        try
        {
            await Task.WhenAll(workers);
        }
        catch (Exception ex)
        {
            workerError = ex;
        }
        if (producerError is not null)
            Rethrow(producerError);
        if (workerError is not null)
            Rethrow(workerError);
    }

    private static void Rethrow(Exception error)
    {
        if (error is AggregateException aggregate)
        {
            var flattened = aggregate.Flatten();
            if (flattened.InnerExceptions.All(inner => inner is OperationCanceledException))
                throw new OperationCanceledException();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(flattened).Throw();
        }
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private async Task ConsumeAsync(ChannelReader<Func<Task>> reader, OrganizationPass stats)
    {
        await foreach (var work in reader.ReadAllAsync(CancellationToken.None))
        {
            var current = Interlocked.Increment(ref stats.Active);
            NotePeak(ref stats.Peak, current);
            try
            {
                await work();
            }
            finally
            {
                Interlocked.Decrement(ref stats.Active);
            }
        }
    }

    private static List<Func<Task>> Interleave(params IEnumerable<Func<Task>>[] lanes)
    {
        var lists = lanes.Select(lane => lane.ToList()).ToArray();
        var result = new List<Func<Task>>();
        for (var index = 0; ; index++)
        {
            var added = false;
            foreach (var lane in lists)
            {
                if (index >= lane.Count) continue;
                result.Add(lane[index]);
                added = true;
            }
            if (!added) return result;
        }
    }

    private static void NotePeak(ref int peak, int current)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref peak);
            if (current <= seen) return;
        }
        while (Interlocked.CompareExchange(ref peak, current, seen) != seen);
    }

    private static bool Nested(string child, string parent, StringComparison comparison) =>
        child.StartsWith(parent + Path.DirectorySeparatorChar, comparison);

    private void RetireCompleted(List<OrganizationEntry> entries)
    {
        var completed = entries.Where(e => e.State == "Complete").Select(e => e.Id).ToHashSet();
        foreach (var marker in entries.Where(e => e.Kind == "Marker" && e.State == "Staged"))
        {
            if (!marker.Members.All(id => completed.Contains(id) || _journal.IsRetired(id))) continue;
            marker.State = "Complete";
            _journal.Save(marker);
        }
        foreach (var entry in entries.Where(e => e.State == "Complete" || (e.Kind == "Pdf" || e.Kind == "Log") && e.State == "Staged"))
            _journal.Retire(entry);
    }

    private static bool HasPending(List<OrganizationEntry> entries, string path) =>
        entries.Any(e => e.Source == path && e.State == "Planned");

    private OrganizationEntry Plan(string source, string kind, string prefix, string directory, string? name = null, string? hash = null)
    {
        var entry = new OrganizationEntry { Source = source, Kind = kind, Prefix = prefix, Hash = hash ?? Hash(source) };
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

    private async Task FinishMoveAsync(OrganizationEntry entry, FileStabilityObservation? known, OrganizationPass stats, CancellationToken token)
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
            if (known is { } observed)
            {
                if (!StillSame(entry.Source, observed, stats)) return;
            }
            else if (await ObserveAsync(entry.Source, stats, token) is null)
                return;

            if (Hash(entry.Source) != entry.Hash) throw new IOException($"Origen cambiado; se conserva: {entry.Source}");
            File.Move(entry.Source, entry.Destination, overwrite: false);
        }
        lock (_sync)
        {
            entry.State = "Staged";
            _journal.Save(entry);
        }
        Interlocked.Increment(ref stats.Moved);
        _logger.LogInformation("Archivo organizado: {Source} -> {Destination}", entry.Source, entry.Destination);
    }

    private Task PublishAsync(OrganizationEntry entry, OrganizationPass stats, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var original = Txt.Match(Path.GetFileName(entry.Source));
            var destination = Path.Combine(_organized, original.Groups["name"].Value);
            if (File.Exists(destination))
            {
                if (entry.State == "Publishing" && entry.CopyDestination is not null && FilePathComparer.ForIdentity.Equals(Path.GetFullPath(entry.CopyDestination), destination) && Hash(destination) == entry.Hash)
                {
                    entry.State = "Complete";
                    _journal.Save(entry);
                    Interlocked.Increment(ref stats.Published);
                    return Task.CompletedTask;
                }
                if (entry.State == "Collision")
                    return Task.CompletedTask;
                _logger.LogWarning("Colisión TXT: no se sobrescribe {Destination}. Original conservado en {Source}", destination, entry.Destination);
                entry.State = "Collision";
                _journal.Save(entry);
                Interlocked.Increment(ref stats.Collisions);
                return Task.CompletedTask;
            }
            if (Hash(entry.Destination) != entry.Hash) throw new IOException($"TXT conservado modificado: {entry.Destination}");
            var temp = Path.Combine(_organized, "." + entry.Id + ".partial");
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
            Interlocked.Increment(ref stats.Published);
            _logger.LogInformation("TXT publicado: {Source} -> {Destination}", entry.Destination, destination);
            return Task.CompletedTask;
        }
    }

    private bool StillSame(string path, FileStabilityObservation observed, OrganizationPass stats)
    {
        if (_readiness.Matches(path, observed)) return true;
        Interlocked.Increment(ref stats.Deferred);
        _logger.LogDebug("Archivo cambiado tras la estabilidad; se aplaza: {Path}", path);
        return false;
    }

    private async Task<FileStabilityObservation?> ObserveAsync(string path, OrganizationPass stats, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.ReadinessTimeoutSeconds));
        var observed = await _readiness.WaitForStableObservationAsync(path, timeout.Token);
        if (observed is not null) return observed;
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref stats.Deferred);
        _logger.LogDebug("Archivo aplazado por estabilidad: {Path}", path);
        return null;
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

    private sealed class OrganizationPass
    {
        public int Examined;
        public int Moved;
        public int Published;
        public int Deferred;
        public int Collisions;
        public int PdfPending;
        public int PdfAmbiguous;
        public int PdfQueries;
        public int Active;
        public int Peak;
    }
}
