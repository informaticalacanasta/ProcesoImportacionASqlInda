using System.Text.Json;
using DbInda.Worker.Alerts;
using DbInda.Worker.Configuration;
using DbInda.Worker.Inbound;
using DbInda.Worker.Tracking;
using Microsoft.Extensions.Options;

namespace DbInda.Worker.Workers;

public sealed class TrackingLifecycleService : IHostedService
{
    private readonly Tracking.ImportTracker _tracker;

    public TrackingLifecycleService(Tracking.ImportTracker tracker)
    {
        _tracker = tracker;
    }

    public Task StartAsync(CancellationToken cancellationToken) => _tracker.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => _tracker.StopAsync("parada del host", cancellationToken);
}

public sealed class ImportHealthWorker : BackgroundService
{
    private readonly Tracking.ImportTracker _tracker;
    private readonly Tracking.ITrackingStore _store;
    private readonly Tracking.TrackingOutbox _outbox;
    private readonly Tracking.FileObservationRegistry _observations;
    private readonly Tracking.InboundActivity _activity;
    private readonly InboundXmlPipeline _pipeline;
    private readonly Processing.SqlRetryScheduler _retries;
    private readonly AlertEngine _alerts;
    private readonly LocalAlertSink _localAlerts;
    private readonly WebhookAlertSink _webhook;
    private readonly TrackingOptions _tracking;
    private readonly PathsOptions _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<ImportHealthWorker> _logger;
    private readonly string _healthPath;
    private DateTimeOffset _lastRetention = DateTimeOffset.MinValue;

    public ImportHealthWorker(
        Tracking.ImportTracker tracker,
        Tracking.ITrackingStore store,
        Tracking.TrackingOutbox outbox,
        Tracking.FileObservationRegistry observations,
        Tracking.InboundActivity activity,
        InboundXmlPipeline pipeline,
        Processing.SqlRetryScheduler retries,
        AlertEngine alerts,
        LocalAlertSink localAlerts,
        WebhookAlertSink webhook,
        IOptions<TrackingOptions> tracking,
        IOptions<PathsOptions> paths,
        TimeProvider time,
        ILogger<ImportHealthWorker> logger)
    {
        _tracker = tracker;
        _store = store;
        _outbox = outbox;
        _observations = observations;
        _activity = activity;
        _pipeline = pipeline;
        _retries = retries;
        _alerts = alerts;
        _localAlerts = localAlerts;
        _webhook = webhook;
        _tracking = tracking.Value;
        _paths = paths.Value;
        _time = time;
        _logger = logger;
        _healthPath = Path.Combine(TrackingPaths.ResolveOutbox(_tracking, _paths), "salud.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_tracking.HeartbeatSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "El ciclo de salud falló. El importador sigue en marcha.");
            }

            try
            {
                await Task.Delay(interval, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var sql = await _store.ProbeAsync(cancellationToken).ConfigureAwait(false);
        _activity.ObserveSql(sql, out var recovered, out var lost);
        if (lost || recovered)
            await _tracker.RecordSqlStateEventAsync(recovered, cancellationToken).ConfigureAwait(false);
        await _tracker.HeartbeatAsync(cancellationToken).ConfigureAwait(false);

        var pending = ListPending(out var inputReachable);
        var present = pending.ToHashSet(FilePathComparer.ForIdentity);
        _observations.ForgetExcept(present);
        var oldest = pending.Count == 0 ? null : _observations.OldestOf(pending);
        _observations.PersistIfDirty();

        var outbox = await _outbox.MeasureAsync(cancellationToken).ConfigureAwait(false);
        var archives = 0;
        var conflicts = 0;
        var archivesKnown = false;
        var conflictsKnown = false;
        IReadOnlyList<(Configuration.ExpectedSourceOptions Source, bool Due)> sources = [];
        if (sql)
        {
            var readout = await _store.ReadOperationalAsync(
                DateTime.Now.AddMinutes(-_tracking.ArchiveStuckMinutes),
                _activity.ConflictWatermarkLocal,
                cancellationToken).ConfigureAwait(false);
            if (readout.QueriesSucceeded)
            {
                archives = readout.ArchivesStuck;
                conflicts = readout.NewConflicts;
                archivesKnown = true;
                conflictsKnown = true;
                _activity.AdvanceConflictWatermark(DateTime.Now);
                sources = EvaluateSources(readout, now);
            }
        }

        var sqlDownFor = _activity.SqlDownSinceUtc is DateTimeOffset since ? now - since : TimeSpan.Zero;
        _activity.ObservePending(pending.Count);
        var stalled = _activity.IsPendingStalled(TimeSpan.FromMinutes(_tracking.PendingWithoutProgressMinutes));
        var snapshot = new Tracking.HealthSnapshot
        {
            ProcessAlive = true,
            SqlReachable = sql,
            SqlDownSustained = !sql && sqlDownFor >= TimeSpan.FromMinutes(_tracking.SqlDownMinutes),
            SqlUnreachableSinceUtc = _activity.SqlDownSinceUtc,
            InputReachable = inputReachable,
            QueueCount = _pipeline.QueuedCount,
            InFlightCount = _pipeline.InFlightCount,
            PendingOnDisk = pending.Count,
            OldestPendingUtc = oldest,
            PendingStalled = stalled,
            RetryWaiting = _retries.PendingCount,
            ArchivesPending = archives,
            OutboxPending = outbox.Pending,
            OldestOutboxUtc = outbox.OldestUtc,
            OutboxSaturated = outbox.Saturated,
            LastScanUtc = _activity.LastScanUtc,
            LastScanOk = _activity.LastScanOk,
            LastAttemptUtc = _activity.LastAttemptUtc,
            LastSuccessUtc = _activity.LastSuccessUtc,
            ExecutionId = _tracker.ExecutionId
        };
        var state = Tracking.HealthJudgement.Judge(snapshot);
        snapshot = new Tracking.HealthSnapshot
        {
            ProcessAlive = snapshot.ProcessAlive,
            ProcessingState = state,
            SqlReachable = snapshot.SqlReachable,
            SqlDownSustained = snapshot.SqlDownSustained,
            SqlUnreachableSinceUtc = snapshot.SqlUnreachableSinceUtc,
            InputReachable = snapshot.InputReachable,
            QueueCount = snapshot.QueueCount,
            InFlightCount = snapshot.InFlightCount,
            PendingOnDisk = snapshot.PendingOnDisk,
            OldestPendingUtc = snapshot.OldestPendingUtc,
            PendingStalled = snapshot.PendingStalled,
            RetryWaiting = snapshot.RetryWaiting,
            ArchivesPending = snapshot.ArchivesPending,
            OutboxPending = snapshot.OutboxPending,
            OldestOutboxUtc = snapshot.OldestOutboxUtc,
            OutboxSaturated = snapshot.OutboxSaturated,
            LastScanUtc = snapshot.LastScanUtc,
            LastScanOk = snapshot.LastScanOk,
            LastAttemptUtc = snapshot.LastAttemptUtc,
            LastSuccessUtc = snapshot.LastSuccessUtc,
            ExecutionId = snapshot.ExecutionId
        };

        _logger.LogInformation(
            "Salud {State}. Proceso vivo. SQL {Sql}. Entrada {Input}. Disco {Disk}. Cola {Queue}. EnVuelo {InFlight} (la cola ya va incluida en el vuelo; no sumar). Reintentos {Retries}. ArchivadosPendientes {Archives}. SeguimientoLocal {Outbox}. UltimoIntento {Attempt}. UltimaImportacion {Success}.",
            state,
            sql ? "accesible" : "inaccesible",
            inputReachable ? "accesible" : "inaccesible",
            pending.Count,
            snapshot.QueueCount,
            snapshot.InFlightCount,
            snapshot.RetryWaiting,
            archivesKnown ? archives : -1,
            outbox.Pending,
            snapshot.LastAttemptUtc,
            snapshot.LastSuccessUtc);

        WriteHealthFile(snapshot);
        var rules = AlertRules.Evaluate(snapshot, _tracking, archivesKnown, archives, conflictsKnown, conflicts, sources, now);
        var policy = new AlertDeliveryPolicy
        {
            WebhookEnabled = _tracking.Alerts.WebhookEnabled && !string.IsNullOrWhiteSpace(_tracking.Alerts.WebhookUrl),
            RetryDelay = TimeSpan.FromSeconds(_tracking.Alerts.RetrySeconds),
            MaxAttempts = _tracking.Alerts.MaxAttempts,
            MaxDelay = TimeSpan.FromMinutes(_tracking.ReminderMinutes)
        };
        var notices = _alerts.Plan(rules, now, TimeSpan.FromMinutes(_tracking.ReminderMinutes), policy);
        await AlertFanOut.DeliverAsync(notices, _localAlerts, _webhook, _alerts, policy, now, _logger, cancellationToken).ConfigureAwait(false);

        if (sql && _tracking.Retention.Enabled && now - _lastRetention >= TimeSpan.FromHours(1))
        {
            _lastRetention = now;
            await RetainAsync(now, cancellationToken).ConfigureAwait(false);
        }
    }

    private List<string> ListPending(out bool reachable)
    {
        reachable = false;
        try
        {
            if (!Directory.Exists(_paths.Input))
                return [];
            var files = new List<string>();
            foreach (var file in Directory.EnumerateFiles(_paths.Input, "*", SearchOption.TopDirectoryOnly))
            {
                if (FilePathNormalizer.HasXmlExtension(file))
                    files.Add(FilePathNormalizer.Normalize(file));
            }

            reachable = true;
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudo leer la carpeta de entrada para el estado de salud.");
            return [];
        }
    }

    private IReadOnlyList<(ExpectedSourceOptions Source, bool Due)> EvaluateSources(Tracking.OperationalReadout readout, DateTimeOffset nowUtc)
    {
        if (_tracking.ExpectedSources.Count == 0 || !BusinessTimeZoneResolver.TryResolve(_tracking.BusinessTimeZone, out var zone))
            return [];

        var results = new List<(ExpectedSourceOptions, bool)>();
        foreach (var source in _tracking.ExpectedSources)
        {
            DateTimeOffset? last = null;
            foreach (var mark in readout.Sources)
            {
                if (mark.Tienda != source.Tienda || mark.Tpv != source.Tpv)
                    continue;
                if (mark.FinIntentoUtc is DateTime fin)
                {
                    var utc = new DateTimeOffset(DateTime.SpecifyKind(fin, DateTimeKind.Utc));
                    if (last is null || utc > last)
                        last = utc;
                }

                if (mark.FechaProcesadoLocal is DateTime local)
                {
                    var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
                    var utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, zone));
                    if (last is null || utc > last)
                        last = utc;
                }
            }

            results.Add((source, ExpectedSourceSchedule.IsAlertDue(source, zone, nowUtc, last)));
        }

        return results;
    }

    private async Task RetainAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now.UtcDateTime.AddDays(-_tracking.Retention.HistoryDays);
        var batch = _tracking.Retention.BatchSize;
        try
        {
            for (var round = 0; round < 20; round++)
            {
                var events = await _store.DeleteBatchAsync(Tracking.TrackingRetentionPolicy.DeleteEventsSql, cutoff, batch, cancellationToken).ConfigureAwait(false);
                var attempts = await _store.DeleteBatchAsync(Tracking.TrackingRetentionPolicy.DeleteAttemptsSql, cutoff, batch, cancellationToken).ConfigureAwait(false);
                var executions = await _store.DeleteBatchAsync(Tracking.TrackingRetentionPolicy.DeleteExecutionsSql, cutoff, batch, cancellationToken).ConfigureAwait(false);
                if (events < batch && attempts < batch && executions < batch)
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "La limpieza del historial de seguimiento falló. No se han tocado tickets ni XML. Si falta el permiso DELETE, la retención debe seguir desactivada.");
        }
    }

    private void WriteHealthFile(Tracking.HealthSnapshot snapshot)
    {
        try
        {
            var directory = Path.GetDirectoryName(_healthPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var temp = _healthPath + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, snapshot);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _healthPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudo escribir {Path}.", _healthPath);
        }
    }
}

public static class TrackingPaths
{
    public static string ResolveOutbox(TrackingOptions tracking, PathsOptions paths)
    {
        if (!string.IsNullOrWhiteSpace(tracking.OutboxDirectory))
            return tracking.OutboxDirectory;
        var logs = string.IsNullOrWhiteSpace(paths.Logs) ? Path.Combine(paths.Input, "logs") : paths.Logs;
        return Path.Combine(logs, "seguimiento");
    }
}
