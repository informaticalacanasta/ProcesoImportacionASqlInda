using System.Diagnostics;
using System.Reflection;
using System.Text;
using DbInda.Worker.Configuration;
using DbInda.Worker.Files;
using DbInda.Worker.Inbound;
using Microsoft.Data.SqlClient;

namespace DbInda.Worker.Tracking;

public sealed class ImportTracker
{
    public const string ApplicationName = "DbInda.Worker";

    private readonly ITrackingStore _store;
    private readonly TrackingOutbox _outbox;
    private readonly TimeProvider _time;
    private readonly TrackingOptions _options;
    private readonly InboundActivity _activity;
    private readonly ILogger<ImportTracker> _logger;
    private readonly Dictionary<Guid, CachedAttempt> _recent = new();
    private readonly object _cacheGate = new();
    private readonly string _version;
    private Guid _executionId;
    private long _sequence;
    private int _started;
    private int _stopped;
    private ExecutionRow? _execution;
    private DateTimeOffset _lastFaultLog = DateTimeOffset.MinValue;
    private bool _needsReconcile = true;
    private const int MaxCachedAttempts = 256;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);

    public int CachedAttemptCount
    {
        get
        {
            lock (_cacheGate)
                return _recent.Count;
        }
    }

    public ImportTracker(
        ITrackingStore store,
        TrackingOutbox outbox,
        TimeProvider time,
        Microsoft.Extensions.Options.IOptions<TrackingOptions> options,
        InboundActivity activity,
        ILogger<ImportTracker> logger)
    {
        _store = store;
        _outbox = outbox;
        _time = time;
        _options = options.Value;
        _activity = activity;
        _logger = logger;
        _version = ReadVersion();
    }

    public Guid ExecutionId => _executionId;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _executionId = Guid.NewGuid();
        var now = _time.GetUtcNow();
        var instance = string.IsNullOrWhiteSpace(_options.InstanceName)
            ? $"{Environment.MachineName}:{Environment.ProcessId}"
            : _options.InstanceName;
        _execution = new ExecutionRow
        {
            IdEjecucion = _executionId,
            Secuencia = NextSequence(),
            NombreAplicacion = ApplicationName,
            NombreInstancia = TextClip.Clip(instance, 200)!,
            Equipo = TextClip.Clip(Environment.MachineName, 128)!,
            IdProceso = Environment.ProcessId,
            VersionAplicacion = _version,
            FechaInicioUtc = now.UtcDateTime,
            FechaSenalUtc = now.UtcDateTime,
            Estado = ExecutionStates.Activa
        };

        await SendExecutionAsync(_execution, cancellationToken).ConfigureAwait(false);
        await RecordServiceEventAsync(TrackingEventTypes.ServicioIniciado, TrackingSeverity.Information, "Arranque del importador.", cancellationToken).ConfigureAwait(false);
        await FlushAndReconcileAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(string reason, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;
        if (_execution is null)
            return;

        var now = _time.GetUtcNow();
        _execution.Secuencia = NextSequence();
        _execution.FechaSenalUtc = now.UtcDateTime;
        _execution.FechaParadaUtc = now.UtcDateTime;
        _execution.Estado = ExecutionStates.Detenida;
        _execution.MotivoFinalizacion = TextClip.Clip(reason, 400);
        await SendExecutionAsync(_execution, cancellationToken).ConfigureAwait(false);
        await RecordServiceEventAsync(TrackingEventTypes.ServicioParado, TrackingSeverity.Information, reason, cancellationToken).ConfigureAwait(false);
        try
        {
            await _outbox.FlushAsync(_store, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFault(ex);
        }
    }

    public async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        if (_stopped != 0 || _execution is null)
            return;
        var now = _time.GetUtcNow();
        _execution.Secuencia = NextSequence();
        _execution.FechaSenalUtc = now.UtcDateTime;
        await SendExecutionAsync(_execution, cancellationToken).ConfigureAwait(false);
        await FlushAndReconcileAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReconcileOpenAttemptsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var staleBefore = _time.GetUtcNow().UtcDateTime.AddMinutes(-_options.StaleSignalMinutes);
            var open = await _store.ListRecoverableAttemptsAsync(_executionId, staleBefore, cancellationToken).ConfigureAwait(false);
            foreach (var attempt in open)
            {
                if (attempt.IdEjecucion == _executionId || attempt.IdEjecucion == Guid.Empty)
                    continue;

                var resultado = TrackingResults.Incierto;
                var mensaje = "Intento abierto de una ejecución anterior. No hay un resultado demostrable; no se marca ni éxito ni fallo de negocio.";
                if (attempt.IdRecepcion is long receptionId && attempt.NumeroIntento is int numero)
                {
                    ReceptionEvidence? evidence;
                    try
                    {
                        evidence = await _store.ReadReceptionEvidenceAsync(receptionId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LogFault(ex);
                        _needsReconcile = true;
                        continue;
                    }

                    if (evidence is not null && evidence.NumeroIntento == numero)
                    {
                        resultado = OpenAttemptResolution.Resolve(evidence.Estado);
                        if (resultado != TrackingResults.Incierto)
                            mensaje = "Cerrado por reconciliación: TICKET_RECEPCION corresponde a este número de intento. La hora de fin no se inventa.";
                    }
                    else if (evidence is not null)
                    {
                        mensaje = "La recepción fue reutilizada por un intento posterior. Este intento no hereda ese resultado.";
                    }
                }

                var closed = false;
                try
                {
                    closed = await _store.TryReconcileOpenAttemptAsync(
                        attempt.IdIntento,
                        attempt.IdEjecucion,
                        staleBefore,
                        resultado,
                        mensaje,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFault(ex);
                    _needsReconcile = true;
                    continue;
                }

                if (!closed)
                    continue;

                await SendEventAsync(new EventRow
                {
                    IdEvento = Guid.NewGuid(),
                    Secuencia = NextSequence(),
                    FechaUtc = _time.GetUtcNow().UtcDateTime,
                    Severidad = resultado == TrackingResults.Incierto ? TrackingSeverity.Warning : TrackingSeverity.Information,
                    Tipo = TrackingEventTypes.Reconciliacion,
                    IdEjecucion = _executionId,
                    IdIntento = attempt.IdIntento,
                    IdRecepcion = attempt.IdRecepcion,
                    RutaOrigen = attempt.RutaOrigen,
                    Fase = TrackingPhases.Reconciliacion,
                    Detalle = TextClip.Clip(mensaje, 2000)
                }, cancellationToken).ConfigureAwait(false);
            }

            _needsReconcile = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _needsReconcile = true;
            LogFault(ex);
        }
    }

    public AttemptSlot OpenAttempt(string path, string fileName, string? hash, int? tienda, int? tpv)
    {
        _activity.NoteAttempt();
        return new AttemptSlot(
            Guid.NewGuid(),
            _time.GetUtcNow(),
            Stopwatch.GetTimestamp(),
            path,
            fileName,
            NormalizeHash(hash),
            tienda,
            tpv);
    }

    public Task RecordConnectionFailureAsync(AttemptSlot slot, Exception exception, DateTimeOffset? nextRetryUtc, CancellationToken cancellationToken)
    {
        var detail = SqlExceptionDetails.ToJson(exception);
        var outcome = new AttemptOutcome
        {
            Resultado = TrackingResults.SqlNoDisponible,
            Fase = TrackingPhases.Conexion,
            CategoriaError = "CONEXION",
            CodigoError = CodeOf(exception),
            Mensaje = exception.Message,
            DetalleTecnico = detail,
            ProximoReintentoUtc = nextRetryUtc,
            EventType = TrackingEventTypes.SqlInaccesible,
            Severidad = TrackingSeverity.Warning
        };
        return RecordOutcomeAsync(slot, outcome, cancellationToken);
    }

    public Task RecordReceptionAttemptAsync(AttemptSlot slot, long receptionId, int attemptNumber, CancellationToken cancellationToken)
    {
        var row = Build(slot, TrackingAttemptKinds.Recepcion, TrackingResults.EnCurso, TrackingPhases.Recepcion, receptionId, attemptNumber, null);
        return SendWithStartEventAsync(row, cancellationToken);
    }

    public async Task<bool> TryConfirmInTransactionAsync(
        SqlConnection connection,
        System.Data.IDbTransaction transaction,
        AttemptSlot slot,
        AttemptOutcome outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            var (row, evento) = BuildFinished(slot, outcome);
            return await _store.TryUpsertAttemptInTransactionAsync(connection, transaction, row, evento, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFault(ex);
            return false;
        }
    }

    public async Task RecordOutcomeAsync(AttemptSlot slot, AttemptOutcome outcome, CancellationToken cancellationToken)
    {
        try
        {
            var (row, evento) = BuildFinished(slot, outcome);
            await SendAttemptAsync(row, cancellationToken).ConfigureAwait(false);
            await SendEventAsync(evento, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFault(ex);
        }
    }

    public async Task CompleteCommittedImportAsync(AttemptSlot slot, AttemptOutcome outcome, bool alreadyPersisted, CancellationToken cancellationToken)
    {
        if (!alreadyPersisted)
            await RecordOutcomeAsync(slot, outcome, cancellationToken).ConfigureAwait(false);
        NoteBusinessOutcome(outcome);
    }

    public void NoteBusinessOutcome(AttemptOutcome outcome)
    {
        if (ImportClassifications.IsNewImport(outcome.Resultado))
            _activity.NoteSuccess();
        else if (outcome.Resultado is TrackingResults.Duplicado or TrackingResults.Conflicto or TrackingResults.ErrorXml or TrackingResults.ErrorPermanente)
            _activity.NoteProgress();
    }

    public async Task NoteRetryScheduledAsync(Guid attemptId, DateTimeOffset nextRetryUtc, CancellationToken cancellationToken)
    {
        try
        {
            AttemptRow? row;
            lock (_cacheGate)
                row = _recent.TryGetValue(attemptId, out var cached) ? cached.Row : null;

            if (row is not null)
            {
                row.Secuencia = NextSequence();
                row.FechaProximoReintentoUtc = nextRetryUtc.UtcDateTime;
                await SendAttemptAsync(row, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _store.ScheduleRetryAsync(attemptId, nextRetryUtc.UtcDateTime, cancellationToken).ConfigureAwait(false);
            }

            await SendEventAsync(new EventRow
            {
                IdEvento = Guid.NewGuid(),
                Secuencia = NextSequence(),
                FechaUtc = _time.GetUtcNow().UtcDateTime,
                Severidad = TrackingSeverity.Warning,
                Tipo = TrackingEventTypes.ErrorReintentoProgramado,
                IdEjecucion = _executionId == Guid.Empty ? null : _executionId,
                IdIntento = attemptId,
                IdRecepcion = row?.IdRecepcion,
                RutaOrigen = row?.RutaOrigen,
                Fase = row?.Fase,
                Detalle = TextClip.Clip($"Próximo reintento UTC {nextRetryUtc:O}.", 2000)
            }, cancellationToken).ConfigureAwait(false);
            lock (_cacheGate)
                _recent.Remove(attemptId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFault(ex);
        }
    }

    public Task RecordArchiveEventAsync(string type, string severity, long receptionId, string? path, string message, CancellationToken cancellationToken)
        => SendEventAsync(new EventRow
        {
            IdEvento = Guid.NewGuid(),
            Secuencia = NextSequence(),
            FechaUtc = _time.GetUtcNow().UtcDateTime,
            Severidad = severity,
            Tipo = type,
            IdEjecucion = _executionId == Guid.Empty ? null : _executionId,
            IdRecepcion = receptionId,
            RutaOrigen = TextClip.Clip(path, 1024),
            Fase = TrackingPhases.Archivo,
            Detalle = TextClip.Clip(message, 2000)
        }, cancellationToken);

    public Task RecordSqlStateEventAsync(bool recovered, CancellationToken cancellationToken)
        => RecordServiceEventAsync(
            recovered ? TrackingEventTypes.SqlRecuperado : TrackingEventTypes.SqlInaccesible,
            recovered ? TrackingSeverity.Information : TrackingSeverity.Error,
            recovered ? "SQL vuelve a aceptar conexiones." : "SQL no acepta conexiones.",
            cancellationToken);

    private async Task SendWithStartEventAsync(AttemptRow row, CancellationToken cancellationToken)
    {
        try
        {
            await SendAttemptAsync(row, cancellationToken).ConfigureAwait(false);
            await SendEventAsync(new EventRow
            {
                IdEvento = Guid.NewGuid(),
                Secuencia = NextSequence(),
                FechaUtc = row.FechaInicioUtc,
                Severidad = TrackingSeverity.Information,
                Tipo = TrackingEventTypes.IntentoIniciado,
                IdEjecucion = row.IdEjecucion,
                IdIntento = row.IdIntento,
                IdRecepcion = row.IdRecepcion,
                RutaOrigen = row.RutaOrigen,
                Fase = row.Fase,
                Detalle = TextClip.Clip($"Intento {row.NumeroIntento} de recepción {row.IdRecepcion}.", 2000)
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFault(ex);
        }
    }

    private (AttemptRow Row, EventRow Event) BuildFinished(AttemptSlot slot, AttemptOutcome outcome)
    {
        var tipo = outcome.IdRecepcion is null ? TrackingAttemptKinds.Conexion : TrackingAttemptKinds.Recepcion;
        var row = Build(slot, tipo, outcome.Resultado, outcome.Fase, outcome.IdRecepcion, outcome.NumeroIntento, outcome);
        var eventId = Guid.NewGuid();
        row.IdDetalleTecnico = eventId;
        var evento = new EventRow
        {
            IdEvento = eventId,
            Secuencia = NextSequence(),
            FechaUtc = row.FechaFinUtc ?? _time.GetUtcNow().UtcDateTime,
            Severidad = outcome.Severidad,
            Tipo = outcome.EventType ?? ImportClassifications.EventFor(outcome.Resultado),
            IdEjecucion = _executionId == Guid.Empty ? null : _executionId,
            IdIntento = slot.Id,
            IdRecepcion = outcome.IdRecepcion,
            IdTicket = outcome.IdTicket,
            RutaOrigen = row.RutaOrigen,
            Fase = outcome.Fase,
            Detalle = TextClip.Clip(outcome.DetalleTecnico ?? outcome.Mensaje, 2000)
        };
        return (row, evento);
    }

    private AttemptRow Build(AttemptSlot slot, string tipo, string resultado, string fase, long? receptionId, int? attemptNumber, AttemptOutcome? outcome)
    {
        lock (_cacheGate)
        {
        if (_recent.TryGetValue(slot.Id, out var cached) && outcome is not null)
        {
            var existing = cached.Row;
            existing.Secuencia = NextSequence();
            existing.IdRecepcion = outcome.IdRecepcion ?? existing.IdRecepcion;
            existing.IdTicket = outcome.IdTicket ?? existing.IdTicket;
            existing.NumeroIntento = outcome.NumeroIntento ?? existing.NumeroIntento;
            existing.TipoIntento = existing.IdRecepcion is null ? TrackingAttemptKinds.Conexion : TrackingAttemptKinds.Recepcion;
            existing.Tienda = outcome.Tienda ?? existing.Tienda ?? slot.Tienda;
            existing.Tpv = outcome.Tpv ?? existing.Tpv ?? slot.Tpv;
            existing.HashSha256 = outcome.Hash ?? existing.HashSha256;
            existing.FechaFinUtc = _time.GetUtcNow().UtcDateTime;
            existing.DuracionMs = Duration(slot);
            existing.Fase = fase;
            existing.Resultado = resultado;
            existing.CategoriaError = TextClip.Clip(outcome.CategoriaError, 40);
            existing.CodigoError = TextClip.Clip(outcome.CodigoError, 64);
            existing.Mensaje = TextClip.Clip(outcome.Mensaje, 1000);
            existing.FechaProximoReintentoUtc = outcome.ProximoReintentoUtc?.UtcDateTime;
            existing.RegistrosConfirmados = outcome.RegistrosConfirmados;
            Remember(existing);
            return existing;
        }
        }

        var row = new AttemptRow
        {
            IdIntento = slot.Id,
            Secuencia = NextSequence(),
            IdEjecucion = _executionId,
            IdRecepcion = receptionId,
            IdTicket = outcome?.IdTicket,
            NombreFichero = TextClip.Clip(Path.GetFileName(slot.FileName), 300) ?? "",
            RutaOrigen = TextClip.Clip(slot.Path, 1024) ?? "",
            CorrelacionOrigen = Sha256FileHasher.ComputeHex(Encoding.UTF8.GetBytes(slot.Path)),
            HashSha256 = outcome?.Hash ?? slot.Hash,
            Tienda = outcome?.Tienda ?? slot.Tienda,
            Tpv = outcome?.Tpv ?? slot.Tpv,
            NumeroIntento = tipo == TrackingAttemptKinds.Conexion ? null : attemptNumber,
            TipoIntento = tipo,
            FechaInicioUtc = slot.StartedUtc.UtcDateTime,
            FechaFinUtc = resultado == TrackingResults.EnCurso ? null : _time.GetUtcNow().UtcDateTime,
            DuracionMs = resultado == TrackingResults.EnCurso ? null : Duration(slot),
            Fase = fase,
            Resultado = resultado,
            CategoriaError = TextClip.Clip(outcome?.CategoriaError, 40),
            CodigoError = TextClip.Clip(outcome?.CodigoError, 64),
            Mensaje = TextClip.Clip(outcome?.Mensaje, 1000),
            FechaProximoReintentoUtc = outcome?.ProximoReintentoUtc?.UtcDateTime,
            RegistrosConfirmados = outcome?.RegistrosConfirmados
        };
        Remember(row);
        return row;
    }

    private void Remember(AttemptRow row)
    {
        var awaitingRetry = row.Resultado is TrackingResults.ErrorSql or TrackingResults.SqlNoDisponible;
        var keep = row.Resultado == TrackingResults.EnCurso || awaitingRetry;
        lock (_cacheGate)
        {
            if (keep)
                _recent[row.IdIntento] = new CachedAttempt(row, _time.GetUtcNow(), awaitingRetry);
            else
                _recent.Remove(row.IdIntento);
            TrimCache();
        }
    }

    private void TrimCache()
    {
        var now = _time.GetUtcNow();
        foreach (var id in _recent.Where(pair => pair.Value.AwaitingRetry && now - pair.Value.TouchedUtc > CacheLifetime).Select(pair => pair.Key).ToList())
            _recent.Remove(id);

        while (_recent.Count > MaxCachedAttempts)
        {
            Guid? oldest = null;
            var oldestTouch = DateTimeOffset.MaxValue;
            foreach (var pair in _recent)
            {
                if (pair.Value.Row.Resultado == TrackingResults.EnCurso)
                    continue;
                if (pair.Value.TouchedUtc < oldestTouch)
                {
                    oldestTouch = pair.Value.TouchedUtc;
                    oldest = pair.Key;
                }
            }

            if (oldest is null || !_recent.Remove(oldest.Value))
                break;
        }
    }

    private async Task FlushAndReconcileAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_needsReconcile)
                _logger.LogDebug("La reconciliación de intentos abiertos sigue pendiente.");
            if (!await _store.ProbeAsync(cancellationToken).ConfigureAwait(false))
            {
                _needsReconcile = true;
                return;
            }

            await _outbox.FlushAsync(_store, cancellationToken).ConfigureAwait(false);
            await ReconcileOpenAttemptsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _needsReconcile = true;
            LogFault(ex);
        }
    }

    private sealed class CachedAttempt(AttemptRow row, DateTimeOffset touchedUtc, bool awaitingRetry)
    {
        public AttemptRow Row { get; } = row;
        public DateTimeOffset TouchedUtc { get; } = touchedUtc;
        public bool AwaitingRetry { get; } = awaitingRetry;
    }

    private async Task SendExecutionAsync(ExecutionRow row, CancellationToken cancellationToken)
    {
        try
        {
            await _store.UpsertExecutionAsync(row, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFault(ex);
            await _outbox.SaveAsync(Envelope("ejecucion", row.IdEjecucion, row.Secuencia, execution: row), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendAttemptAsync(AttemptRow row, CancellationToken cancellationToken)
    {
        try
        {
            await _store.UpsertAttemptAsync(row, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFault(ex);
            await _outbox.SaveAsync(Envelope("intento", row.IdIntento, row.Secuencia, attempt: row), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendEventAsync(EventRow row, CancellationToken cancellationToken)
    {
        try
        {
            await _store.InsertEventAsync(row, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFault(ex);
            await _outbox.SaveAsync(Envelope("evento", row.IdEvento, row.Secuencia, evento: row), cancellationToken).ConfigureAwait(false);
        }
    }

    private Task RecordServiceEventAsync(string type, string severity, string message, CancellationToken cancellationToken)
        => SendEventAsync(new EventRow
        {
            IdEvento = Guid.NewGuid(),
            Secuencia = NextSequence(),
            FechaUtc = _time.GetUtcNow().UtcDateTime,
            Severidad = severity,
            Tipo = type,
            IdEjecucion = _executionId == Guid.Empty ? null : _executionId,
            Fase = TrackingPhases.Servicio,
            Detalle = TextClip.Clip(message, 2000)
        }, cancellationToken);

    private OutboxEnvelope Envelope(string kind, Guid id, long sequence, ExecutionRow? execution = null, AttemptRow? attempt = null, EventRow? evento = null)
        => new()
        {
            Kind = kind,
            Id = id,
            Sequence = sequence,
            CreatedUtc = _time.GetUtcNow(),
            Execution = execution,
            Attempt = attempt,
            Event = evento
        };

    private long NextSequence() => Interlocked.Increment(ref _sequence);

    private static int Duration(AttemptSlot slot)
        => (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(slot.StartedTimestamp).TotalMilliseconds);

    private static string? NormalizeHash(string? hash)
        => hash is { Length: 64 } && hash.All(Uri.IsHexDigit) ? hash : null;

    private static string? CodeOf(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
                return sql.Number.ToString();
        }

        return exception.GetType().Name;
    }

    private static string ReadVersion()
    {
        var assembly = typeof(ImportTracker).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational?.Split('+')[0];
        return TextClip.Clip(string.IsNullOrWhiteSpace(version) ? assembly.GetName().Version?.ToString() ?? "desconocida" : version, 64)!;
    }

    private void LogFault(Exception exception)
    {
        var now = _time.GetUtcNow();
        if (now - _lastFaultLog < TimeSpan.FromMinutes(1))
            return;
        _lastFaultLog = now;
        _logger.LogWarning(exception, "El seguimiento no pudo escribir en SQL. Se conserva el error de negocio y, si el disco lo permite, el evento local.");
    }
}
