using System.Data;
using System.Net;
using System.Text;
using System.Text.Json;
using DbInda.Worker.Alerts;
using DbInda.Worker.Configuration;
using DbInda.Worker.Logging;
using DbInda.Worker.Models;
using DbInda.Worker.Tracking;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DbInda.Tests.Tracking;

public sealed partial class TrackingBehaviorTests
{
    [Fact]
    public async Task Bandeja_reenvia_y_un_duplicado_no_crea_otro_evento()
    {
        using var dir = new TempDir();
        var store = new MemoryTrackingStore();
        var outbox = NewOutbox(dir.Path, 100, 1_000_000);
        var id = Guid.NewGuid();
        var row = Event(id, 1);
        Assert.True(await outbox.SaveAsync(Envelope("evento", id, 1, evento: row), CancellationToken.None));
        Assert.Equal(1, await outbox.FlushAsync(store, CancellationToken.None));
        Assert.True(await outbox.SaveAsync(Envelope("evento", id, 1, evento: row), CancellationToken.None));
        Assert.Equal(1, await outbox.FlushAsync(store, CancellationToken.None));
        Assert.Single(store.Events);
        Assert.Empty(Directory.EnumerateFiles(dir.Path, "*.json"));
    }

    [Fact]
    public async Task Entrada_corrupta_no_descarta_el_resto()
    {
        using var dir = new TempDir();
        var store = new MemoryTrackingStore();
        var outbox = NewOutbox(dir.Path, 100, 1_000_000);
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "evento-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.json"), "{no es json");
        var id = Guid.NewGuid();
        await outbox.SaveAsync(Envelope("evento", id, 2, evento: Event(id, 2)), CancellationToken.None);
        Assert.Equal(1, await outbox.FlushAsync(store, CancellationToken.None));
        Assert.Single(store.Events);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(dir.Path, "corruptos")));
    }

    [Fact]
    public async Task Escrituras_concurrentes_conservan_todos_los_eventos()
    {
        using var dir = new TempDir();
        var outbox = NewOutbox(dir.Path, 1000, 10_000_000);
        var ids = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()).ToArray();
        await Task.WhenAll(ids.Select((id, index) => outbox.SaveAsync(Envelope("evento", id, index + 1, evento: Event(id, index + 1)), CancellationToken.None)));
        var store = new MemoryTrackingStore();
        Assert.Equal(30, await outbox.FlushAsync(store, CancellationToken.None));
        Assert.Equal(30, store.Events.Count);
    }

    [Fact]
    public async Task Al_llegar_al_limite_no_se_borran_los_pendientes()
    {
        using var dir = new TempDir();
        var outbox = NewOutbox(dir.Path, 2, 10_000_000);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        Assert.True(await outbox.SaveAsync(Envelope("evento", first, 1, evento: Event(first, 1)), CancellationToken.None));
        Assert.True(await outbox.SaveAsync(Envelope("evento", second, 2, evento: Event(second, 2)), CancellationToken.None));
        Assert.False(await outbox.SaveAsync(Envelope("evento", third, 3, evento: Event(third, 3)), CancellationToken.None));
        var metrics = await outbox.MeasureAsync(CancellationToken.None);
        Assert.Equal(2, metrics.Pending);
        Assert.True(metrics.Saturated);
    }

    [Fact]
    public async Task Fallo_de_conexion_no_inventa_recepcion_y_el_reintento_conserva_el_error()
    {
        var store = new MemoryTrackingStore();
        var tracker = Tracker(store, out var dir);
        using (dir)
        {
            await tracker.StartAsync(CancellationToken.None);
            var slot = tracker.OpenAttempt(@"C:\entrada\a.xml", "a.xml", null, 4, 2);
            await tracker.RecordConnectionFailureAsync(slot, new InvalidOperationException("sql caída"), DateTimeOffset.Parse("2026-09-24T10:00:00Z"), CancellationToken.None);
            var failed = Assert.Single(store.Attempts.Values);
            Assert.Null(failed.IdRecepcion);
            Assert.Null(failed.NumeroIntento);
            Assert.Equal(TrackingAttemptKinds.Conexion, failed.TipoIntento);
            Assert.Equal(TrackingResults.SqlNoDisponible, failed.Resultado);

            var retry = tracker.OpenAttempt(@"C:\entrada\a.xml", "a.xml", null, 4, 2);
            await tracker.RecordReceptionAttemptAsync(retry, 15, 2, CancellationToken.None);
            await tracker.RecordOutcomeAsync(retry, new AttemptOutcome
            {
                Resultado = TrackingResults.Importado,
                Fase = TrackingPhases.Insercion,
                IdRecepcion = 15,
                IdTicket = 90,
                NumeroIntento = 2,
                RegistrosConfirmados = 3
            }, CancellationToken.None);

            Assert.Equal(TrackingResults.SqlNoDisponible, store.Attempts[slot.Id].Resultado);
            Assert.Equal(TrackingResults.Importado, store.Attempts[retry.Id].Resultado);
            Assert.Contains(store.Events.Values, item => item.IdIntento == slot.Id && item.Tipo == TrackingEventTypes.SqlInaccesible);
        }
    }

    [Fact]
    public async Task La_confirmacion_dentro_de_la_transaccion_no_es_visible_hasta_el_commit()
    {
        var store = new MemoryTrackingStore { HoldTransactions = true };
        var tracker = Tracker(store, out var dir);
        using (dir)
        {
            await tracker.StartAsync(CancellationToken.None);
            var slot = tracker.OpenAttempt(@"C:\entrada\b.xml", "b.xml", null, 1, 1);
            await tracker.RecordReceptionAttemptAsync(slot, 8, 1, CancellationToken.None);
            var tx = new FakeTransaction();
            using var connection = new SqlConnection("Server=127.0.0.1,1;Connect Timeout=1;TrustServerCertificate=true");
            var wrote = await tracker.TryConfirmInTransactionAsync(connection, tx, slot, new AttemptOutcome
            {
                Resultado = TrackingResults.Importado,
                Fase = TrackingPhases.Insercion,
                IdRecepcion = 8,
                IdTicket = 3,
                NumeroIntento = 1,
                RegistrosConfirmados = 4
            }, CancellationToken.None);
            Assert.True(wrote);
            Assert.Equal(TrackingResults.EnCurso, store.Attempts[slot.Id].Resultado);
            store.Rollback(tx);
            Assert.Equal(TrackingResults.EnCurso, store.Attempts[slot.Id].Resultado);
            await tracker.TryConfirmInTransactionAsync(connection, tx, slot, new AttemptOutcome
            {
                Resultado = TrackingResults.Importado,
                Fase = TrackingPhases.Insercion,
                IdRecepcion = 8,
                IdTicket = 3,
                NumeroIntento = 1
            }, CancellationToken.None);
            store.Commit(tx);
            Assert.Equal(TrackingResults.Importado, store.Attempts[slot.Id].Resultado);
            Assert.Equal(3, store.Attempts[slot.Id].IdTicket);
        }
    }

    [Fact]
    public async Task Un_fallo_del_seguimiento_no_reemplaza_la_excepcion_original()
    {
        var store = new MemoryTrackingStore { FailWrites = true };
        var tracker = Tracker(store, out var dir);
        using (dir)
        {
            var original = new InvalidOperationException("error de negocio");
            var slot = tracker.OpenAttempt(@"C:\entrada\c.xml", "c.xml", null, null, null);
            var thrown = await RecordedException(async () =>
            {
                try
                {
                    throw original;
                }
                catch (Exception ex)
                {
                    await tracker.RecordOutcomeAsync(slot, new AttemptOutcome
                    {
                        Resultado = TrackingResults.ErrorSql,
                        Fase = TrackingPhases.Insercion,
                        Mensaje = ex.Message
                    }, CancellationToken.None);
                    throw;
                }
            });
            Assert.Same(original, thrown);
        }
    }

    [Fact]
    public async Task Archivado_fallido_no_cambia_el_resultado_de_la_importacion()
    {
        var store = new MemoryTrackingStore();
        var tracker = Tracker(store, out var dir);
        using (dir)
        {
            var slot = tracker.OpenAttempt(@"C:\entrada\d.xml", "d.xml", null, 1, 1);
            await tracker.RecordOutcomeAsync(slot, new AttemptOutcome
            {
                Resultado = TrackingResults.Importado,
                Fase = TrackingPhases.Insercion,
                IdRecepcion = 4,
                IdTicket = 5,
                NumeroIntento = 1
            }, CancellationToken.None);
            await tracker.RecordArchiveEventAsync(TrackingEventTypes.ArchivadoFallido, TrackingSeverity.Error, 4, @"C:\entrada\d.xml", "disco lleno", CancellationToken.None);
            Assert.Equal(TrackingResults.Importado, store.Attempts[slot.Id].Resultado);
            Assert.Contains(store.Events.Values, item => item.Tipo == TrackingEventTypes.ArchivadoFallido);
        }
    }

    [Fact]
    public async Task Reconciliacion_no_inventa_exito_si_no_hay_prueba()
    {
        var store = new MemoryTrackingStore();
        store.Attempts[Guid.NewGuid()] = new AttemptRow
        {
            IdIntento = store.Attempts.Keys.FirstOrDefault(),
            Resultado = TrackingResults.EnCurso,
            TipoIntento = TrackingAttemptKinds.Recepcion,
            NumeroIntento = 1,
            NombreFichero = "z.xml",
            RutaOrigen = @"C:\entrada\z.xml",
            CorrelacionOrigen = new string('A', 64),
            Fase = TrackingPhases.Insercion,
            FechaInicioUtc = DateTime.UtcNow
        };
        var id = Guid.NewGuid();
        store.Attempts.Clear();
        store.Attempts[id] = new AttemptRow
        {
            IdIntento = id,
            Resultado = TrackingResults.EnCurso,
            TipoIntento = TrackingAttemptKinds.Recepcion,
            NumeroIntento = 1,
            IdRecepcion = 77,
            NombreFichero = "z.xml",
            RutaOrigen = @"C:\entrada\z.xml",
            CorrelacionOrigen = new string('B', 64),
            Fase = TrackingPhases.Insercion,
            FechaInicioUtc = DateTime.UtcNow,
            Secuencia = 1
        };
        var origin = Guid.NewGuid();
        store.Executions[origin] = new ExecutionRow
        {
            IdEjecucion = origin,
            Estado = ExecutionStates.Detenida,
            FechaParadaUtc = DateTime.UtcNow.AddMinutes(-10),
            FechaSenalUtc = DateTime.UtcNow.AddMinutes(-10)
        };
        store.Attempts[id].IdEjecucion = origin;
        store.ReceptionEstado[77] = ReceptionStatuses.Pendiente;
        store.ReceptionAttempts[77] = 1;
        var tracker = Tracker(store, out var dir);
        using (dir)
        {
            await tracker.ReconcileOpenAttemptsAsync(CancellationToken.None);
            Assert.Equal(TrackingResults.Incierto, store.Attempts[id].Resultado);
            Assert.Null(store.Attempts[id].FechaFinUtc);
        }
    }

    [Fact]
    public void Ultima_importacion_ignora_duplicados_conflictos_y_errores()
    {
        var latest = ImportClassifications.LatestSuccessfulImport(
            [
                DateTimeOffset.Parse("2026-09-24T08:00:00Z"),
                DateTimeOffset.Parse("2026-09-24T09:00:00Z"),
                DateTimeOffset.Parse("2026-09-24T07:00:00Z")
            ],
            [TrackingResults.Duplicado, TrackingResults.Conflicto, TrackingResults.Importado]);
        Assert.Equal(DateTimeOffset.Parse("2026-09-24T07:00:00Z"), latest);
        Assert.False(ImportClassifications.IsNewImport(TrackingResults.Duplicado));
        Assert.False(ImportClassifications.IsNewImport(TrackingResults.Conflicto));
        Assert.True(ImportClassifications.IsNewImport(TrackingResults.ImportadoConAdvertencias));
    }

    [Theory]
    [InlineData(false, true, ReceptionStatuses.Procesado, CommitDecision.TreatAsFailure)]
    [InlineData(true, false, null, CommitDecision.Uncertain)]
    [InlineData(true, true, ReceptionStatuses.Procesado, CommitDecision.UsePersisted)]
    [InlineData(true, true, ReceptionStatuses.Pendiente, CommitDecision.TreatAsFailure)]
    public void Confirmacion_incierta_no_afirma_sin_lectura(bool started, bool read, string? estado, CommitDecision expected)
    {
        Assert.Equal(expected, CommitUncertainty.Decide(started, read, estado));
    }

    [Fact]
    public void Resolucion_de_intento_abierto_solo_usa_estados_terminales()
    {
        Assert.Equal(TrackingResults.Importado, OpenAttemptResolution.Resolve(ReceptionStatuses.Procesado));
        Assert.Equal(TrackingResults.Incierto, OpenAttemptResolution.Resolve(ReceptionStatuses.Pendiente));
        Assert.Equal(TrackingResults.Incierto, OpenAttemptResolution.Resolve(null));
        Assert.Equal(TrackingResults.ErrorSql, OpenAttemptResolution.Resolve(ReceptionStatuses.ErrorSql));
    }

    [Fact]
    public void Retencion_protege_abiertos_y_no_toca_tickets()
    {
        Assert.True(TrackingRetentionPolicy.IsProtectedAttempt(TrackingResults.EnCurso));
        Assert.True(TrackingRetentionPolicy.IsProtectedAttempt(TrackingResults.Incierto));
        Assert.False(TrackingRetentionPolicy.IsProtectedAttempt(TrackingResults.Importado));
        Assert.Contains("EN_CURSO", TrackingRetentionPolicy.DeleteAttemptsSql, StringComparison.Ordinal);
        Assert.Contains("NOT EXISTS", TrackingRetentionPolicy.DeleteEventsSql, StringComparison.Ordinal);
        Assert.DoesNotContain("dbo.TICKET", TrackingRetentionPolicy.DeleteAttemptsSql, StringComparison.Ordinal);
        Assert.DoesNotContain("dbo.TICKET", TrackingRetentionPolicy.DeleteEventsSql, StringComparison.Ordinal);
        Assert.DoesNotContain("dbo.TICKET", TrackingRetentionPolicy.DeleteExecutionsSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Horario_inactivo_y_gracia_no_alertan()
    {
        Assert.True(BusinessTimeZoneResolver.TryResolve("Europe/Madrid", out var zone));
        var source = new ExpectedSourceOptions
        {
            Tienda = 1,
            Tpv = 1,
            ActiveDays = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
            Start = "08:00",
            End = "22:00",
            GraceMinutes = 60,
            MaxSilenceMinutes = 120
        };
        var sunday = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
        Assert.False(ExpectedSourceSchedule.IsAlertDue(source, zone, sunday, null));
        var mondayEarly = new DateTimeOffset(2026, 9, 28, 6, 30, 0, TimeSpan.Zero);
        Assert.False(ExpectedSourceSchedule.IsAlertDue(source, zone, mondayEarly, null));
        var mondayLate = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        Assert.True(ExpectedSourceSchedule.IsAlertDue(source, zone, mondayLate, null));
        Assert.False(ExpectedSourceSchedule.IsAlertDue(source, zone, mondayLate, mondayLate.AddMinutes(-30)));
    }

    [Fact]
    public void Alertas_se_deduplican_y_avisan_la_recuperacion()
    {
        using var dir = new TempDir();
        var engine = new AlertEngine(new AlertStateStore(Path.Combine(dir.Path, "estado.json"), NullLogger<AlertStateStore>.Instance));
        var now = DateTimeOffset.Parse("2026-09-24T10:00:00Z");
        var open = engine.Plan([new RuleResult("sql-inaccesible", true, new AlertSignal("sql-inaccesible", "Error", "caído"))], now, TimeSpan.FromMinutes(30));
        Assert.Equal("abierta", Assert.Single(open).Status);
        engine.MarkNotified("sql-inaccesible", now);
        var again = engine.Plan([new RuleResult("sql-inaccesible", true, new AlertSignal("sql-inaccesible", "Error", "caído"))], now.AddMinutes(5), TimeSpan.FromMinutes(30));
        Assert.Empty(again);
        var recovered = engine.Plan([new RuleResult("sql-inaccesible", true, null)], now.AddMinutes(6), TimeSpan.FromMinutes(30));
        Assert.Equal("recuperada", Assert.Single(recovered).Status);
        engine.MarkNotified("sql-inaccesible", now.AddMinutes(6));
        var quiet = engine.Plan([new RuleResult("sql-inaccesible", true, null)], now.AddMinutes(7), TimeSpan.FromMinutes(30));
        Assert.Empty(quiet);
    }

    [Fact]
    public void Una_regla_no_evaluada_no_cierra_la_incidencia()
    {
        using var dir = new TempDir();
        var engine = new AlertEngine(new AlertStateStore(Path.Combine(dir.Path, "estado.json"), NullLogger<AlertStateStore>.Instance));
        var now = DateTimeOffset.Parse("2026-09-24T10:00:00Z");
        engine.Plan([new RuleResult("conflictos-nuevos", true, new AlertSignal("conflictos-nuevos", "Warning", "2"))], now, TimeSpan.FromMinutes(30));
        engine.MarkNotified("conflictos-nuevos", now);
        var skipped = engine.Plan([new RuleResult("conflictos-nuevos", false, null)], now.AddMinutes(1), TimeSpan.FromMinutes(30));
        Assert.Empty(skipped);
        Assert.Contains(engine.Incidents, item => item.Key == "conflictos-nuevos" && item.Open);
    }

    [Fact]
    public void Proceso_vivo_no_equivale_a_procesamiento_sano()
    {
        var blocked = HealthJudgement.Judge(new HealthSnapshot { ProcessAlive = true, SqlDownSustained = true, InputReachable = true });
        Assert.Equal(HealthJudgement.Bloqueado, blocked);
        var waiting = HealthJudgement.Judge(new HealthSnapshot { ProcessAlive = true, SqlReachable = true, InputReachable = true });
        Assert.Equal(HealthJudgement.EsperandoDatos, waiting);
    }

    [Fact]
    public void Log_json_incluye_utc_y_no_lanza_si_no_puede_escribir()
    {
        var line = JsonLogLine.Format(DateTimeOffset.Parse("2026-09-24T08:00:00Z"), LogLevel.Warning, "cat", default, "Password=secreto; resto", new InvalidOperationException("fallo"), null);
        Assert.Contains("2026-09-24T08:00:00.000Z", line, StringComparison.Ordinal);
        Assert.Contains("Password=***", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secreto", line, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(line);
        Assert.Equal("Warning", json.RootElement.GetProperty("level").GetString());

        var blocked = Path.Combine(Path.GetTempPath(), "dbinda-log-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocked, "no soy un directorio");
        var provider = new DailyFileLoggerProvider(blocked, 31);
        var logger = provider.CreateLogger("test");
        var exception = Record.Exception(() => logger.LogWarning("no debe romper"));
        Assert.Null(exception);
        provider.Dispose();
        exception = Record.Exception(() => logger.LogWarning("tampoco tras cerrar"));
        Assert.Null(exception);
        File.Delete(blocked);
    }

    [Fact]
    public async Task Webhook_desactivado_no_envia()
    {
        var handler = new RecordingHandler();
        var sink = new WebhookAlertSink(
            Options.Create(new TrackingOptions { Alerts = new TrackingAlertOptions { WebhookEnabled = false, WebhookUrl = "http://127.0.0.1:9/alerta" } }),
            NullLogger<WebhookAlertSink>.Instance,
            handler);
        await sink.PublishAsync(new AlertNotice("sql-inaccesible", "abierta", "Error", "caído", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), CancellationToken.None);
        Assert.Equal(0, handler.Calls);
    }

    private static TrackingOutbox NewOutbox(string path, int files, long bytes)
        => new(path, files, bytes, NullLogger<TrackingOutbox>.Instance);

    private static ImportTracker Tracker(MemoryTrackingStore store, out TempDir dir, InboundActivity? activity = null, TimeProvider? time = null)
    {
        dir = new TempDir();
        var clock = time ?? TimeProvider.System;
        return new ImportTracker(
            store,
            NewOutbox(dir.Path, 100, 1_000_000),
            clock,
            Options.Create(new TrackingOptions()),
            activity ?? new InboundActivity(clock),
            NullLogger<ImportTracker>.Instance);
    }

    private static EventRow Event(Guid id, long sequence) => new()
    {
        IdEvento = id,
        Secuencia = sequence,
        FechaUtc = DateTime.UtcNow,
        Tipo = TrackingEventTypes.Error,
        Severidad = TrackingSeverity.Warning
    };

    private static OutboxEnvelope Envelope(string kind, Guid id, long sequence, EventRow? evento = null) => new()
    {
        Kind = kind,
        Id = id,
        Sequence = sequence,
        CreatedUtc = DateTimeOffset.UnixEpoch,
        Event = evento
    };

    private static async Task<Exception> RecordedException(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Se esperaba una excepción.");
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dbinda-tracking-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class FakeTransaction : IDbTransaction
    {
        public IDbConnection? Connection => null;
        public IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        public void Commit() { }
        public void Dispose() { }
        public void Rollback() { }
    }

    private sealed class MemoryTrackingStore : ITrackingStore
    {
        private readonly List<(IDbTransaction Transaction, AttemptRow Attempt, EventRow? Event)> _held = [];

        private readonly object _sync = new();

        public Dictionary<Guid, AttemptRow> Attempts { get; } = [];
        public Dictionary<Guid, EventRow> Events { get; } = [];
        public Dictionary<Guid, ExecutionRow> Executions { get; } = [];
        public Dictionary<long, string> ReceptionEstado { get; } = [];
        public Dictionary<long, int> ReceptionAttempts { get; } = [];
        public bool FailWrites { get; set; }
        public bool FailProbe { get; set; }
        public bool FailList { get; set; }
        public bool HoldTransactions { get; set; }

        public Task UpsertExecutionAsync(ExecutionRow row, CancellationToken cancellationToken)
        {
            if (FailWrites)
                throw new IOException("sql");
            lock (_sync)
                Executions[row.IdEjecucion] = row;
            return Task.CompletedTask;
        }

        public Task UpsertAttemptAsync(AttemptRow row, CancellationToken cancellationToken)
        {
            if (FailWrites)
                throw new IOException("sql");
            Upsert(row);
            return Task.CompletedTask;
        }

        public Task<bool> TryUpsertAttemptInTransactionAsync(SqlConnection connection, IDbTransaction transaction, AttemptRow row, EventRow? evento, CancellationToken cancellationToken)
        {
            if (HoldTransactions)
            {
                _held.Add((transaction, Clone(row), evento is null ? null : CloneEvent(evento)));
                return Task.FromResult(true);
            }

            Upsert(row);
            if (evento is not null)
                Events.TryAdd(evento.IdEvento, evento);
            return Task.FromResult(true);
        }

        public Task InsertEventAsync(EventRow row, CancellationToken cancellationToken)
        {
            if (FailWrites)
                throw new IOException("sql");
            lock (_sync)
                Events.TryAdd(row.IdEvento, row);
            return Task.CompletedTask;
        }

        public Task<bool> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(!FailWrites && !FailProbe);

        public Task<IReadOnlyList<AttemptRow>> ListOpenAttemptsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AttemptRow>>(Attempts.Values.Where(row => row.Resultado == TrackingResults.EnCurso).ToList());

        public Task<IReadOnlyList<AttemptRow>> ListRecoverableAttemptsAsync(Guid currentExecutionId, DateTime staleSignalBeforeUtc, CancellationToken cancellationToken)
        {
            if (FailList)
                throw new IOException("sql");
            lock (_sync)
            {
                var rows = Attempts.Values.Where(row =>
                    row.Resultado == TrackingResults.EnCurso
                    && row.IdEjecucion != currentExecutionId
                    && row.IdEjecucion != Guid.Empty
                    && Executions.TryGetValue(row.IdEjecucion, out var execution)
                    && (execution.FechaParadaUtc is not null || execution.FechaSenalUtc < staleSignalBeforeUtc)).ToList();
                return Task.FromResult<IReadOnlyList<AttemptRow>>(rows);
            }
        }

        public Task<bool> TryReconcileOpenAttemptAsync(Guid attemptId, Guid originExecutionId, DateTime staleSignalBeforeUtc, string resultado, string mensaje, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (!Attempts.TryGetValue(attemptId, out var current))
                    return Task.FromResult(false);
                if (current.Resultado != TrackingResults.EnCurso || current.IdEjecucion != originExecutionId)
                    return Task.FromResult(false);
                if (!Executions.TryGetValue(originExecutionId, out var execution))
                    return Task.FromResult(false);
                if (execution.FechaParadaUtc is null && execution.FechaSenalUtc >= staleSignalBeforeUtc)
                    return Task.FromResult(false);

                var updated = Clone(current);
                updated.Resultado = resultado;
                updated.Fase = TrackingPhases.Reconciliacion;
                updated.Mensaje = mensaje;
                Attempts[attemptId] = updated;
                return Task.FromResult(true);
            }
        }

        public Task<ReceptionEvidence?> ReadReceptionEvidenceAsync(long idRecepcion, CancellationToken cancellationToken)
        {
            if (!ReceptionEstado.TryGetValue(idRecepcion, out var estado))
                return Task.FromResult<ReceptionEvidence?>(null);
            return Task.FromResult<ReceptionEvidence?>(new ReceptionEvidence
            {
                Estado = estado,
                NumeroIntento = ReceptionAttempts.TryGetValue(idRecepcion, out var numero) ? numero : 0
            });
        }

        public Task<bool> ScheduleRetryAsync(Guid attemptId, DateTime nextRetryUtc, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (!Attempts.TryGetValue(attemptId, out var current))
                    return Task.FromResult(false);
                if (current.Resultado is not (TrackingResults.ErrorSql or TrackingResults.SqlNoDisponible))
                    return Task.FromResult(false);
                var updated = Clone(current);
                updated.FechaProximoReintentoUtc = nextRetryUtc;
                Attempts[attemptId] = updated;
                return Task.FromResult(true);
            }
        }

        public Task<string?> ReadReceptionEstadoAsync(long idRecepcion, CancellationToken cancellationToken)
            => Task.FromResult(ReceptionEstado.TryGetValue(idRecepcion, out var estado) ? estado : null);

        public Task<OperationalReadout> ReadOperationalAsync(DateTime archiveStuckBeforeLocal, DateTime conflictsSinceLocal, CancellationToken cancellationToken)
            => Task.FromResult(new OperationalReadout { QueriesSucceeded = true });

        public Task<int> DeleteBatchAsync(string sql, DateTime cutoffUtc, int batch, CancellationToken cancellationToken)
            => Task.FromResult(0);

        public void Commit(IDbTransaction transaction)
        {
            foreach (var item in _held.Where(item => ReferenceEquals(item.Transaction, transaction)).ToList())
            {
                Upsert(item.Attempt);
                if (item.Event is not null)
                    Events.TryAdd(item.Event.IdEvento, item.Event);
            }

            _held.RemoveAll(item => ReferenceEquals(item.Transaction, transaction));
        }

        public void Rollback(IDbTransaction transaction)
            => _held.RemoveAll(item => ReferenceEquals(item.Transaction, transaction));

        private void Upsert(AttemptRow row)
        {
            lock (_sync)
            {
                if (Attempts.TryGetValue(row.IdIntento, out var current)
                    && !ImportClassifications.AllowsSequenceUpdate(current.Resultado, current.Secuencia, row.Resultado, row.Secuencia))
                    return;
                Attempts[row.IdIntento] = Clone(row);
            }
        }

        private static AttemptRow Clone(AttemptRow row) => new()
        {
            IdIntento = row.IdIntento,
            Secuencia = row.Secuencia,
            IdEjecucion = row.IdEjecucion,
            IdRecepcion = row.IdRecepcion,
            IdTicket = row.IdTicket,
            NombreFichero = row.NombreFichero,
            RutaOrigen = row.RutaOrigen,
            CorrelacionOrigen = row.CorrelacionOrigen,
            HashSha256 = row.HashSha256,
            Tienda = row.Tienda,
            Tpv = row.Tpv,
            NumeroIntento = row.NumeroIntento,
            TipoIntento = row.TipoIntento,
            FechaInicioUtc = row.FechaInicioUtc,
            FechaFinUtc = row.FechaFinUtc,
            DuracionMs = row.DuracionMs,
            Fase = row.Fase,
            Resultado = row.Resultado,
            CategoriaError = row.CategoriaError,
            CodigoError = row.CodigoError,
            Mensaje = row.Mensaje,
            IdDetalleTecnico = row.IdDetalleTecnico,
            FechaProximoReintentoUtc = row.FechaProximoReintentoUtc,
            RegistrosConfirmados = row.RegistrosConfirmados
        };

        private static EventRow CloneEvent(EventRow row) => new()
        {
            IdEvento = row.IdEvento,
            Secuencia = row.Secuencia,
            FechaUtc = row.FechaUtc,
            Severidad = row.Severidad,
            Tipo = row.Tipo,
            IdEjecucion = row.IdEjecucion,
            IdIntento = row.IdIntento,
            IdRecepcion = row.IdRecepcion,
            IdTicket = row.IdTicket,
            RutaOrigen = row.RutaOrigen,
            Fase = row.Fase,
            Detalle = row.Detalle
        };
    }
}
