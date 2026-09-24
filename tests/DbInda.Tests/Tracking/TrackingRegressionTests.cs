using System.Data;
using System.Net;
using System.Text.Json;
using DbInda.Worker.Alerts;
using DbInda.Worker.Configuration;
using DbInda.Worker.Models;
using DbInda.Worker.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DbInda.Tests.Tracking;

public sealed partial class TrackingBehaviorTests
{
    [Fact]
    public async Task Flush_conserva_los_archivos_de_estado_y_no_los_mide()
    {
        using var dir = new TempDir();
        var outbox = NewOutbox(dir.Path, 100, 1_000_000);
        var salud = Path.Combine(dir.Path, "salud.json");
        var observaciones = Path.Combine(dir.Path, "observaciones.json");
        var alertas = Path.Combine(dir.Path, "alertas-estado.json");
        await File.WriteAllTextAsync(salud, new string('s', 2000));
        await File.WriteAllTextAsync(observaciones, """{"a":1}""");
        await File.WriteAllTextAsync(alertas, """[{"Key":"sql-inaccesible"}]""");
        var id = Guid.NewGuid();
        Assert.True(await outbox.SaveAsync(Envelope("evento", id, 1, evento: Event(id, 1)), CancellationToken.None));

        var before = await outbox.MeasureAsync(CancellationToken.None);
        Assert.Equal(1, before.Pending);
        Assert.True(before.PendingBytes < 2000);

        var store = new MemoryTrackingStore();
        Assert.Equal(1, await outbox.FlushAsync(store, CancellationToken.None));
        Assert.Equal(new string('s', 2000), await File.ReadAllTextAsync(salud));
        Assert.Equal("""{"a":1}""", await File.ReadAllTextAsync(observaciones));
        Assert.Equal("""[{"Key":"sql-inaccesible"}]""", await File.ReadAllTextAsync(alertas));
        Assert.Single(store.Events);

        var after = await outbox.MeasureAsync(CancellationToken.None);
        Assert.Equal(0, after.Pending);
        Assert.Equal(0, after.PendingBytes);
    }

    [Fact]
    public async Task Un_sobre_invalido_se_aparta_y_no_bloquea_los_siguientes()
    {
        using var dir = new TempDir();
        var outbox = NewOutbox(dir.Path, 100, 1_000_000);
        var pending = Path.Combine(dir.Path, "pendientes");
        Directory.CreateDirectory(pending);
        var badId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var goodId = Guid.NewGuid();
        await File.WriteAllTextAsync(
            Path.Combine(pending, $"evento-{badId:N}.json"),
            JsonSerializer.Serialize(new { Kind = "otro", Id = badId, Sequence = 1, CreatedUtc = DateTimeOffset.UnixEpoch }));
        await File.WriteAllTextAsync(
            Path.Combine(pending, $"evento-{missingId:N}.json"),
            JsonSerializer.Serialize(new OutboxEnvelope { Kind = "evento", Id = missingId, Sequence = 2, CreatedUtc = DateTimeOffset.UnixEpoch }));
        await File.WriteAllTextAsync(Path.Combine(pending, "evento-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.json"), "{no es json");
        await outbox.SaveAsync(Envelope("evento", goodId, 3, evento: Event(goodId, 3)), CancellationToken.None);

        var store = new MemoryTrackingStore();
        Assert.Equal(1, await outbox.FlushAsync(store, CancellationToken.None));
        Assert.Equal(goodId, Assert.Single(store.Events.Keys));
        Assert.Equal(3, Directory.EnumerateFiles(Path.Combine(dir.Path, "corruptos")).Count());
        Assert.Empty(Directory.EnumerateFiles(pending, "*.json"));
    }

    [Fact]
    public async Task Un_evento_antiguo_en_la_raiz_se_reenvia_una_sola_vez()
    {
        using var dir = new TempDir();
        var outbox = NewOutbox(dir.Path, 100, 1_000_000);
        var id = Guid.NewGuid();
        var legacy = Path.Combine(dir.Path, $"evento-{id:N}.json");
        await File.WriteAllTextAsync(legacy, JsonSerializer.Serialize(Envelope("evento", id, 7, evento: Event(id, 7))));
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "salud.json"), "estado");
        var buried = Path.Combine(dir.Path, "corruptos", $"evento-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(buried)!);
        await File.WriteAllTextAsync(buried, JsonSerializer.Serialize(Envelope("evento", Guid.NewGuid(), 1, evento: Event(Guid.NewGuid(), 1))));

        var store = new MemoryTrackingStore();
        Assert.Equal(1, await outbox.FlushAsync(store, CancellationToken.None));
        Assert.False(File.Exists(legacy));
        Assert.False(File.Exists(Path.Combine(dir.Path, "pendientes", Path.GetFileName(legacy))));
        Assert.Equal("estado", await File.ReadAllTextAsync(Path.Combine(dir.Path, "salud.json")));
        Assert.True(File.Exists(buried));
        Assert.Single(store.Events);

        store.FailWrites = true;
        var kept = Guid.NewGuid();
        Assert.True(await outbox.SaveAsync(Envelope("evento", kept, 8, evento: Event(kept, 8)), CancellationToken.None));
        Assert.Equal(0, await outbox.FlushAsync(store, CancellationToken.None));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(dir.Path, "pendientes"), "*.json"));
    }

    [Fact]
    public async Task El_commit_con_seguimiento_ya_persistido_actualiza_la_salud_sin_duplicar_el_evento()
    {
        var clock = new ManualTime();
        var activity = new InboundActivity(clock);
        var store = new MemoryTrackingStore { HoldTransactions = true };
        var tracker = Tracker(store, out var dir, activity, clock);
        using (dir)
        {
            var slot = tracker.OpenAttempt(@"C:\entrada\ok.xml", "ok.xml", null, 1, 1);
            await tracker.RecordReceptionAttemptAsync(slot, 11, 1, CancellationToken.None);
            var outcome = Imported(11, 20);
            var tx = new FakeTransaction();
            using var connection = new Microsoft.Data.SqlClient.SqlConnection("Server=127.0.0.1,1;Connect Timeout=1;TrustServerCertificate=true");
            Assert.True(await tracker.TryConfirmInTransactionAsync(connection, tx, slot, outcome, CancellationToken.None));
            Assert.Null(activity.LastSuccessUtc);
            store.Rollback(tx);
            Assert.Null(activity.LastSuccessUtc);
            Assert.Equal(TrackingResults.EnCurso, store.Attempts[slot.Id].Resultado);

            Assert.True(await tracker.TryConfirmInTransactionAsync(connection, tx, slot, outcome, CancellationToken.None));
            store.Commit(tx);
            clock.UtcNow = clock.UtcNow.AddMinutes(1);
            await tracker.CompleteCommittedImportAsync(slot, outcome, alreadyPersisted: true, CancellationToken.None);
            Assert.Equal(clock.UtcNow, activity.LastSuccessUtc);
            Assert.Equal(1, store.Events.Values.Count(item => item.Tipo == TrackingEventTypes.ImportacionConfirmada));
        }
    }

    [Fact]
    public async Task El_commit_con_seguimiento_posterior_actualiza_la_ultima_importacion()
    {
        var clock = new ManualTime();
        var activity = new InboundActivity(clock);
        var store = new MemoryTrackingStore();
        var tracker = Tracker(store, out var dir, activity, clock);
        using (dir)
        {
            var slot = tracker.OpenAttempt(@"C:\entrada\ok2.xml", "ok2.xml", null, 1, 1);
            await tracker.RecordReceptionAttemptAsync(slot, 12, 1, CancellationToken.None);
            clock.UtcNow = clock.UtcNow.AddMinutes(2);
            await tracker.CompleteCommittedImportAsync(slot, Imported(12, 21), alreadyPersisted: false, CancellationToken.None);
            Assert.Equal(clock.UtcNow, activity.LastSuccessUtc);
            Assert.Equal(TrackingResults.Importado, store.Attempts[slot.Id].Resultado);
            Assert.Equal(1, store.Events.Values.Count(item => item.Tipo == TrackingEventTypes.ImportacionConfirmada));
        }
    }

    [Fact]
    public async Task Un_commit_incierto_duplicado_o_conflicto_no_cuentan_como_importacion_nueva()
    {
        var clock = new ManualTime();
        var activity = new InboundActivity(clock);
        var store = new MemoryTrackingStore();
        var tracker = Tracker(store, out var dir, activity, clock);
        using (dir)
        {
            Assert.Equal(CommitDecision.Uncertain, CommitUncertainty.Decide(true, readSucceeded: false, estado: null));
            var uncertain = tracker.OpenAttempt(@"C:\entrada\u.xml", "u.xml", null, 1, 1);
            await tracker.RecordOutcomeAsync(uncertain, new AttemptOutcome
            {
                Resultado = TrackingResults.Incierto,
                Fase = TrackingPhases.Insercion,
                IdRecepcion = 30,
                NumeroIntento = 1
            }, CancellationToken.None);
            tracker.NoteBusinessOutcome(new AttemptOutcome { Resultado = TrackingResults.Incierto, Fase = TrackingPhases.Insercion });
            Assert.Null(activity.LastSuccessUtc);

            var duplicate = tracker.OpenAttempt(@"C:\entrada\d.xml", "d.xml", null, 1, 1);
            await tracker.CompleteCommittedImportAsync(duplicate, new AttemptOutcome
            {
                Resultado = TrackingResults.Duplicado,
                Fase = TrackingPhases.Insercion,
                IdRecepcion = 31,
                NumeroIntento = 1
            }, false, CancellationToken.None);
            var conflict = tracker.OpenAttempt(@"C:\entrada\c.xml", "c.xml", null, 1, 1);
            await tracker.CompleteCommittedImportAsync(conflict, new AttemptOutcome
            {
                Resultado = TrackingResults.Conflicto,
                Fase = TrackingPhases.Insercion,
                IdRecepcion = 32,
                NumeroIntento = 1
            }, false, CancellationToken.None);
            Assert.Null(activity.LastSuccessUtc);
            var proven = tracker.OpenAttempt(@"C:\entrada\ok.xml", "ok.xml", null, 1, 1);
            await tracker.CompleteCommittedImportAsync(proven, Imported(33, 33), false, CancellationToken.None);
            var earlier = activity.LastSuccessUtc;
            Assert.NotNull(earlier);
            clock.UtcNow = clock.UtcNow.AddMinutes(-5);
            tracker.NoteBusinessOutcome(Imported(34, 34));
            Assert.Equal(earlier, activity.LastSuccessUtc);
        }
    }

    [Fact]
    public void Un_archivo_recien_llegado_no_dispara_bloqueo_inmediato()
    {
        var clock = new ManualTime();
        var activity = new InboundActivity(clock);
        activity.ObservePending(0);
        clock.UtcNow = clock.UtcNow.AddHours(3);
        activity.ObservePending(1);
        var window = TimeSpan.FromMinutes(10);
        Assert.False(activity.IsPendingStalled(window));
        activity.NoteProgress();
        clock.UtcNow = clock.UtcNow.AddMinutes(9);
        Assert.False(activity.IsPendingStalled(window));
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        Assert.True(activity.IsPendingStalled(window));

        var rules = AlertRules.Evaluate(
            new HealthSnapshot { PendingOnDisk = 1, PendingStalled = false, OldestPendingUtc = clock.UtcNow, InputReachable = true, SqlReachable = true },
            new TrackingOptions(),
            false,
            0,
            false,
            0,
            [],
            clock.UtcNow);
        Assert.DoesNotContain(rules, rule => rule.Key == "pendientes-sin-progreso" && rule.Signal is not null);
    }

    [Fact]
    public async Task Una_secuencia_alta_de_otra_ejecucion_se_reconcilia_y_un_evento_atrasado_no_la_revierte()
    {
        var store = new MemoryTrackingStore();
        var origin = StoppedExecution(store);
        var id = OpenAttempt(store, origin, 500, 40, 1);
        store.ReceptionEstado[40] = ReceptionStatuses.Procesado;
        store.ReceptionAttempts[40] = 1;
        var tracker = Tracker(store, out var dir);
        using (dir)
        {
            await tracker.StartAsync(CancellationToken.None);
            Assert.Equal(TrackingResults.Importado, store.Attempts[id].Resultado);
            Assert.Equal(500, store.Attempts[id].Secuencia);
            Assert.Null(store.Attempts[id].FechaFinUtc);
            Assert.Equal(1, store.Events.Values.Count(item => item.Tipo == TrackingEventTypes.Reconciliacion && item.IdIntento == id));

            await store.UpsertAttemptAsync(new AttemptRow
            {
                IdIntento = id,
                Secuencia = 3,
                IdEjecucion = origin,
                Resultado = TrackingResults.EnCurso,
                TipoIntento = TrackingAttemptKinds.Recepcion,
                NumeroIntento = 1,
                NombreFichero = "a.xml",
                RutaOrigen = @"C:\entrada\a.xml",
                CorrelacionOrigen = new string('C', 64)
            }, CancellationToken.None);
            await store.UpsertAttemptAsync(new AttemptRow
            {
                IdIntento = id,
                Secuencia = 900,
                IdEjecucion = origin,
                Resultado = TrackingResults.EnCurso,
                TipoIntento = TrackingAttemptKinds.Recepcion,
                NumeroIntento = 1,
                NombreFichero = "a.xml",
                RutaOrigen = @"C:\entrada\a.xml",
                CorrelacionOrigen = new string('C', 64)
            }, CancellationToken.None);
            Assert.Equal(TrackingResults.Importado, store.Attempts[id].Resultado);
            Assert.Equal(500, store.Attempts[id].Secuencia);
        }
    }

    [Fact]
    public async Task Una_ejecucion_activa_no_se_cierra_y_dos_reconciliadores_no_se_contradicen()
    {
        var store = new MemoryTrackingStore();
        var live = Guid.NewGuid();
        store.Executions[live] = new ExecutionRow
        {
            IdEjecucion = live,
            Estado = ExecutionStates.Activa,
            FechaSenalUtc = DateTime.UtcNow
        };
        var liveAttempt = OpenAttempt(store, live, 8, 41, 1);
        var origin = StoppedExecution(store);
        var stale = OpenAttempt(store, origin, 9, 42, 1);
        store.ReceptionEstado[42] = ReceptionStatuses.Pendiente;
        store.ReceptionAttempts[42] = 1;

        var first = Tracker(store, out var dir1);
        var second = Tracker(store, out var dir2);
        using (dir1)
        using (dir2)
        {
            await Task.WhenAll(
                first.ReconcileOpenAttemptsAsync(CancellationToken.None),
                second.ReconcileOpenAttemptsAsync(CancellationToken.None));
            Assert.Equal(TrackingResults.EnCurso, store.Attempts[liveAttempt].Resultado);
            Assert.Equal(TrackingResults.Incierto, store.Attempts[stale].Resultado);
            Assert.Equal(1, store.Events.Values.Count(item => item.IdIntento == stale && item.Tipo == TrackingEventTypes.Reconciliacion));
        }
    }

    [Fact]
    public async Task Si_sql_cae_al_arrancar_la_reconciliacion_ocurre_al_recuperarse()
    {
        var store = new MemoryTrackingStore { FailProbe = true };
        var origin = StoppedExecution(store);
        var id = OpenAttempt(store, origin, 15, 50, 1);
        store.ReceptionEstado[50] = ReceptionStatuses.Pendiente;
        store.ReceptionAttempts[50] = 1;
        var tracker = Tracker(store, out var dir);
        using (dir)
        {
            await tracker.StartAsync(CancellationToken.None);
            Assert.Equal(TrackingResults.EnCurso, store.Attempts[id].Resultado);
            store.FailProbe = false;
            await tracker.HeartbeatAsync(CancellationToken.None);
            Assert.Equal(TrackingResults.Incierto, store.Attempts[id].Resultado);
            Assert.Null(store.Attempts[id].FechaFinUtc);
        }
    }

    [Fact]
    public async Task Un_intento_abierto_reenviado_desde_disco_entra_en_la_recuperacion()
    {
        using var dir = new TempDir();
        var store = new MemoryTrackingStore { FailProbe = true };
        var origin = StoppedExecution(store);
        var id = Guid.NewGuid();
        var outbox = NewOutbox(dir.Path, 100, 1_000_000);
        var tracker = new ImportTracker(
            store,
            outbox,
            TimeProvider.System,
            Options.Create(new TrackingOptions()),
            new InboundActivity(TimeProvider.System),
            NullLogger<ImportTracker>.Instance);
        await outbox.SaveAsync(new OutboxEnvelope
        {
            Kind = "intento",
            Id = id,
            Sequence = 4,
            CreatedUtc = DateTimeOffset.UnixEpoch,
            Attempt = new AttemptRow
            {
                IdIntento = id,
                Secuencia = 4,
                IdEjecucion = origin,
                IdRecepcion = 60,
                NumeroIntento = 1,
                Resultado = TrackingResults.EnCurso,
                TipoIntento = TrackingAttemptKinds.Recepcion,
                NombreFichero = "reenvio.xml",
                RutaOrigen = @"C:\entrada\reenvio.xml",
                CorrelacionOrigen = new string('D', 64),
                Fase = TrackingPhases.Insercion,
                FechaInicioUtc = DateTime.UtcNow
            }
        }, CancellationToken.None);
        store.ReceptionEstado[60] = ReceptionStatuses.Procesado;
        store.ReceptionAttempts[60] = 2;

        await tracker.StartAsync(CancellationToken.None);
        Assert.False(store.Attempts.ContainsKey(id));
        store.FailProbe = false;
        await tracker.HeartbeatAsync(CancellationToken.None);
        Assert.Equal(TrackingResults.Incierto, store.Attempts[id].Resultado);
        Assert.Null(store.Attempts[id].FechaFinUtc);
        Assert.Contains("reutilizada", store.Attempts[id].Mensaje, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Los_intentos_terminados_no_permanecen_en_cache_y_el_reintento_sigue_registrandose()
    {
        var clock = new ManualTime();
        var store = new MemoryTrackingStore();
        var tracker = Tracker(store, out var dir, time: clock);
        using (dir)
        {
            var active = tracker.OpenAttempt(@"C:\entrada\activo.xml", "activo.xml", null, 1, 1);
            await tracker.RecordReceptionAttemptAsync(active, 70, 1, CancellationToken.None);

            for (var i = 0; i < 40; i++)
            {
                var slot = tracker.OpenAttempt($@"C:\entrada\t{i}.xml", $"t{i}.xml", null, 1, 1);
                await tracker.CompleteCommittedImportAsync(slot, Imported(80 + i, 80 + i), false, CancellationToken.None);
            }

            Assert.Equal(1, tracker.CachedAttemptCount);
            Assert.Equal(TrackingResults.EnCurso, store.Attempts[active.Id].Resultado);

            var ids = new Guid[257];
            for (var i = 0; i < ids.Length; i++)
            {
                clock.UtcNow = clock.UtcNow.AddTicks(1);
                var slot = tracker.OpenAttempt($@"C:\entrada\e{i}.xml", $"e{i}.xml", null, 1, 1);
                await tracker.RecordConnectionFailureAsync(slot, new InvalidOperationException("sql"), null, CancellationToken.None);
                ids[i] = slot.Id;
            }

            Assert.Equal(256, tracker.CachedAttemptCount);
            var when = DateTimeOffset.Parse("2026-09-24T12:00:00Z");
            await tracker.NoteRetryScheduledAsync(ids[0], when, CancellationToken.None);
            Assert.Equal(256, tracker.CachedAttemptCount);
            Assert.Equal(when.UtcDateTime, store.Attempts[ids[0]].FechaProximoReintentoUtc);
            await tracker.NoteRetryScheduledAsync(ids[^1], when, CancellationToken.None);
            Assert.Equal(when.UtcDateTime, store.Attempts[ids[^1]].FechaProximoReintentoUtc);
            Assert.Contains(store.Events.Values, item => item.IdIntento == ids[0] && item.Tipo == TrackingEventTypes.ErrorReintentoProgramado);

            var errors = 0;
            await Parallel.ForEachAsync(Enumerable.Range(0, 30), async (i, token) =>
            {
                try
                {
                    var slot = tracker.OpenAttempt($@"C:\entrada\p{i}.xml", $"p{i}.xml", null, 1, 1);
                    await tracker.CompleteCommittedImportAsync(slot, Imported(400 + i, 400 + i), false, token);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref errors);
                }
            });
            Assert.Equal(0, errors);
            Assert.True(tracker.CachedAttemptCount <= 256);
            Assert.Equal(TrackingResults.EnCurso, store.Attempts[active.Id].Resultado);
        }
    }

    [Fact]
    public async Task Un_webhook_fallido_queda_pendiente_y_se_reintenta_con_el_mismo_identificador()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "alertas-estado.json");
        var now = DateTimeOffset.Parse("2026-09-24T10:00:00Z");
        var policy = new AlertDeliveryPolicy
        {
            WebhookEnabled = true,
            RetryDelay = TimeSpan.FromSeconds(60),
            MaxAttempts = 3,
            MaxDelay = TimeSpan.FromMinutes(30)
        };
        var engine = new AlertEngine(new AlertStateStore(path, NullLogger<AlertStateStore>.Instance));
        var open = engine.Plan([Signal("sql-inaccesible", "caído")], now, TimeSpan.FromMinutes(30), policy);
        var notice = Assert.Single(open);
        Assert.Contains(AlertChannels.Local, notice.ChannelsDue!);
        Assert.Contains(AlertChannels.Webhook, notice.ChannelsDue!);

        var handler = new ScriptedHandler(HttpStatusCode.InternalServerError);
        var webhook = Webhook(handler);
        var local = new LocalAlertSink(Path.Combine(dir.Path, "alertas.log"), NullLogger<LocalAlertSink>.Instance);
        await AlertFanOut.DeliverAsync([notice], local, webhook, engine, policy, now, NullLogger.Instance, CancellationToken.None);
        var incident = Assert.Single(engine.Incidents);
        Assert.Equal("enviado", incident.Deliveries.Single(item => item.Channel == AlertChannels.Local).State);
        var pending = incident.Deliveries.Single(item => item.Channel == AlertChannels.Webhook);
        Assert.Equal("pendiente", pending.State);
        Assert.Equal(notice.NoticeId, pending.NoticeId);

        var restarted = new AlertEngine(new AlertStateStore(path, NullLogger<AlertStateStore>.Instance));
        var tooSoon = restarted.Plan([Signal("sql-inaccesible", "caído")], now.AddSeconds(30), TimeSpan.FromMinutes(30), policy);
        Assert.Empty(tooSoon);
        var retry = Assert.Single(restarted.Plan([Signal("sql-inaccesible", "caído")], now.AddSeconds(60), TimeSpan.FromMinutes(30), policy));
        Assert.Equal(notice.NoticeId, retry.NoticeId);
        Assert.Equal([AlertChannels.Webhook], retry.ChannelsDue);

        handler.Next = HttpStatusCode.NoContent;
        await AlertFanOut.DeliverAsync([retry], local, webhook, restarted, policy, now.AddSeconds(60), NullLogger.Instance, CancellationToken.None);
        Assert.Equal("enviado", Assert.Single(restarted.Incidents).Deliveries.Single(item => item.Channel == AlertChannels.Webhook).State);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(notice.NoticeId, NoticeId(handler.Bodies[0]));
        Assert.Equal(notice.NoticeId, NoticeId(handler.Bodies[1]));
    }

    [Fact]
    public async Task La_recuperacion_fallida_no_se_pierde_y_la_reapertura_no_queda_bloqueada()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "alertas-estado.json");
        var now = DateTimeOffset.Parse("2026-09-24T11:00:00Z");
        var policy = new AlertDeliveryPolicy { WebhookEnabled = true, RetryDelay = TimeSpan.FromSeconds(60), MaxAttempts = 2 };
        var engine = new AlertEngine(new AlertStateStore(path, NullLogger<AlertStateStore>.Instance));
        var opened = Assert.Single(engine.Plan([Signal("archivado-atascado", "atascado")], now, TimeSpan.FromMinutes(30), policy));
        engine.MarkChannel(opened.Key, opened.NoticeId, AlertChannels.Local, true, now, policy);
        engine.MarkChannel(opened.Key, opened.NoticeId, AlertChannels.Webhook, true, now, policy);

        var recovery = Assert.Single(engine.Plan([new RuleResult("archivado-atascado", true, null)], now.AddMinutes(1), TimeSpan.FromMinutes(30), policy));
        engine.MarkChannel(recovery.Key, recovery.NoticeId, AlertChannels.Local, true, now.AddMinutes(1), policy);
        var scheduled = engine.MarkChannel(recovery.Key, recovery.NoticeId, AlertChannels.Webhook, false, now.AddMinutes(1), policy);
        Assert.Equal(AlertChannelUpdate.Scheduled, scheduled);
        Assert.True(Assert.Single(engine.Incidents).RecoveryPending);

        var reloaded = new AlertEngine(new AlertStateStore(path, NullLogger<AlertStateStore>.Instance));
        Assert.True(Assert.Single(reloaded.Incidents).RecoveryPending);
        var reopened = Assert.Single(reloaded.Plan([Signal("archivado-atascado", "otra vez")], now.AddMinutes(2), TimeSpan.FromMinutes(30), policy));
        Assert.Equal("abierta", reopened.Status);
        Assert.NotEqual(recovery.NoticeId, reopened.NoticeId);
        Assert.True(Assert.Single(reloaded.Incidents).Open);
    }

    [Fact]
    public async Task Un_webhook_desactivado_no_crea_pendientes_y_el_abandono_queda_registrado()
    {
        using var dir = new TempDir();
        var engine = new AlertEngine(new AlertStateStore(Path.Combine(dir.Path, "estado.json"), NullLogger<AlertStateStore>.Instance));
        var now = DateTimeOffset.Parse("2026-09-24T12:00:00Z");
        var disabled = new AlertDeliveryPolicy { WebhookEnabled = false, RetryDelay = TimeSpan.FromSeconds(60), MaxAttempts = 1 };
        var notice = Assert.Single(engine.Plan([Signal("sql-inaccesible", "caído")], now, TimeSpan.FromMinutes(30), disabled));
        Assert.Equal([AlertChannels.Local], notice.ChannelsDue);
        var handler = new ScriptedHandler(HttpStatusCode.InternalServerError);
        await AlertFanOut.DeliverAsync(
            [notice],
            new LocalAlertSink(Path.Combine(dir.Path, "alertas.log"), NullLogger<LocalAlertSink>.Instance),
            Webhook(handler),
            engine,
            disabled,
            now,
            NullLogger.Instance,
            CancellationToken.None);
        Assert.Equal(0, handler.Calls);
        Assert.DoesNotContain(Assert.Single(engine.Incidents).Deliveries, item => item.Channel == AlertChannels.Webhook);

        var abandoning = new AlertDeliveryPolicy { WebhookEnabled = true, RetryDelay = TimeSpan.FromSeconds(1), MaxAttempts = 1 };
        var recoveryEngine = new AlertEngine(new AlertStateStore(Path.Combine(dir.Path, "otro.json"), NullLogger<AlertStateStore>.Instance));
        var opened = Assert.Single(recoveryEngine.Plan([Signal("sql-inaccesible", "caído")], now, TimeSpan.FromMinutes(30), abandoning));
        recoveryEngine.MarkChannel(opened.Key, opened.NoticeId, AlertChannels.Local, true, now, abandoning);
        recoveryEngine.MarkChannel(opened.Key, opened.NoticeId, AlertChannels.Webhook, true, now, abandoning);
        var recovery = Assert.Single(recoveryEngine.Plan([new RuleResult("sql-inaccesible", true, null)], now.AddMinutes(1), TimeSpan.FromMinutes(30), abandoning));
        Assert.Equal(AlertChannelUpdate.Abandoned, recoveryEngine.MarkChannel(recovery.Key, recovery.NoticeId, AlertChannels.Webhook, false, now.AddMinutes(1), abandoning));
        recoveryEngine.MarkChannel(recovery.Key, recovery.NoticeId, AlertChannels.Local, true, now.AddMinutes(1), abandoning);
        Assert.Empty(recoveryEngine.Incidents);
    }

    private static Guid StoppedExecution(MemoryTrackingStore store)
    {
        var id = Guid.NewGuid();
        store.Executions[id] = new ExecutionRow
        {
            IdEjecucion = id,
            Estado = ExecutionStates.Detenida,
            FechaParadaUtc = DateTime.UtcNow.AddMinutes(-10),
            FechaSenalUtc = DateTime.UtcNow.AddMinutes(-10)
        };
        return id;
    }

    private static Guid OpenAttempt(MemoryTrackingStore store, Guid executionId, long sequence, long receptionId, int attemptNumber)
    {
        var id = Guid.NewGuid();
        store.Attempts[id] = new AttemptRow
        {
            IdIntento = id,
            Secuencia = sequence,
            IdEjecucion = executionId,
            IdRecepcion = receptionId,
            NumeroIntento = attemptNumber,
            Resultado = TrackingResults.EnCurso,
            TipoIntento = TrackingAttemptKinds.Recepcion,
            NombreFichero = "a.xml",
            RutaOrigen = @"C:\entrada\a.xml",
            CorrelacionOrigen = new string('E', 64),
            Fase = TrackingPhases.Insercion,
            FechaInicioUtc = DateTime.UtcNow
        };
        return id;
    }

    private static AttemptOutcome Imported(long receptionId, long ticketId) => new()
    {
        Resultado = TrackingResults.Importado,
        Fase = TrackingPhases.Insercion,
        IdRecepcion = receptionId,
        IdTicket = ticketId,
        NumeroIntento = 1,
        RegistrosConfirmados = 2
    };

    private static RuleResult Signal(string key, string summary)
        => new(key, true, new AlertSignal(key, "Warning", summary));

    private static WebhookAlertSink Webhook(HttpMessageHandler handler)
        => new(
            Options.Create(new TrackingOptions { Alerts = new TrackingAlertOptions { WebhookEnabled = true, WebhookUrl = "http://127.0.0.1:9/alerta" } }),
            NullLogger<WebhookAlertSink>.Instance,
            handler);

    private static string? NoticeId(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("NoticeId").GetString();

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-09-24T10:00:00Z");
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public ScriptedHandler(HttpStatusCode first) => Next = first;
        public HttpStatusCode Next { get; set; }
        public int Calls { get; private set; }
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(Next);
        }
    }
}
