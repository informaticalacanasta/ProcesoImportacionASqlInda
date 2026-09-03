using DbInda.Worker.Configuration;
using DbInda.Worker.Inbound;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbInda.Tests.Inbound;

public sealed class InboundXmlPipelineTests
{
    [Fact]
    public async Task Fichero_ya_existente_al_arrancar_se_descubre_por_el_scanner()
    {
        using var folder = new TempFolder();
        var path = folder.WriteXml("existente.xml");
        var (pipeline, processor) = PipelineFactory.Create();
        var scanner = new InputDirectoryScanner(new ImmediateTimeProvider(), NullLogger<InputDirectoryScanner>.Instance);
        pipeline.Start();

        scanner.ScanOnce(folder.Path, p => pipeline.Submit(p, CancellationToken.None));

        await TestWait.UntilAsync(() => processor.CompletedCount == 1);
        Assert.Contains(processor.CompletedPaths, p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

        await pipeline.ShutdownAsync();
    }

    [Fact]
    public async Task Evento_watcher_duplicado_no_procesa_dos_veces()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new RecordingProcessor(async (_, _) => await hold.Task);
        var (pipeline, _) = PipelineFactory.Create(processor: processor);
        pipeline.Start();
        const string path = @"C:\DbInda\Entrada\duplicado.xml";

        pipeline.Submit(path, CancellationToken.None);
        pipeline.Submit(path, CancellationToken.None);

        await TestWait.UntilAsync(() => processor.StartedCount == 1);
        Assert.Equal(1, processor.StartedCount);

        hold.SetResult();
        await TestWait.UntilAsync(() => processor.CompletedCount == 1 && pipeline.InFlightCount == 0);
        Assert.Equal(1, processor.StartedCount);

        await pipeline.ShutdownAsync();
    }

    [Fact]
    public async Task Misma_ruta_descubierta_por_scanner_y_watcher_se_procesa_una_vez()
    {
        using var folder = new TempFolder();
        folder.WriteXml("ambos.xml");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new RecordingProcessor(async (_, _) => await hold.Task);
        var (pipeline, _) = PipelineFactory.Create(processor: processor);
        var scanner = new InputDirectoryScanner(new ImmediateTimeProvider(), NullLogger<InputDirectoryScanner>.Instance);
        pipeline.Start();

        var discovered = new List<string>();
        scanner.ScanOnce(folder.Path, path =>
        {
            discovered.Add(path);
            pipeline.Submit(path, CancellationToken.None);
        });
        Assert.Single(discovered);
        pipeline.Submit(discovered[0], CancellationToken.None);

        await TestWait.UntilAsync(() => processor.StartedCount == 1);
        Assert.Equal(1, processor.StartedCount);

        hold.SetResult();
        await TestWait.UntilAsync(() => processor.CompletedCount == 1 && pipeline.InFlightCount == 0);
        Assert.Equal(1, processor.StartedCount);

        await pipeline.ShutdownAsync();
    }

    [Fact]
    public async Task Fichero_creciendo_no_se_encola()
    {
        var t1 = new DateTime(2026, 8, 15, 10, 0, 0, DateTimeKind.Utc);
        var probe = new ScriptedFileProbe
        {
            Script =
            [
                (true, 10, t1),
                (true, 20, t1.AddSeconds(1)),
                (true, 30, t1.AddSeconds(2))
            ]
        };
        var (pipeline, processor) = PipelineFactory.Create(
            PipelineFactory.FastOptions(stableChecks: 3),
            probe);
        pipeline.Start();
        pipeline.Submit(@"C:\DbInda\Entrada\creciendo.xml", CancellationToken.None);

        await pipeline.ShutdownAsync();
        Assert.Equal(0, processor.StartedCount);
    }

    [Fact]
    public async Task Fichero_estable_se_encola_y_procesa()
    {
        using var folder = new TempFolder();
        var path = folder.WriteXml("ok.xml");
        var (pipeline, processor) = PipelineFactory.Create(
            PipelineFactory.FastOptions(stableChecks: 2),
            new FileSystemStabilityProbe());
        pipeline.Start();
        pipeline.Submit(path, CancellationToken.None);

        await TestWait.UntilAsync(() => processor.CompletedCount == 1);
        await pipeline.ShutdownAsync();
    }

    [Fact]
    public async Task Queue_capacity_aplica_backpressure_sin_exceder_la_cola()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new RecordingProcessor(async (_, _) =>
        {
            started.TrySetResult();
            await hold.Task;
        });
        var (pipeline, _) = PipelineFactory.Create(
            PipelineFactory.FastOptions(maxConcurrency: 1, queueCapacity: 1),
            processor: processor);
        pipeline.Start();

        pipeline.Submit(@"C:\DbInda\Entrada\q1.xml", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        pipeline.Submit(@"C:\DbInda\Entrada\q2.xml", CancellationToken.None);
        await TestWait.UntilAsync(() => pipeline.QueuedCount == 1 && pipeline.InFlightCount == 2);

        pipeline.Submit(@"C:\DbInda\Entrada\q3.xml", CancellationToken.None);
        await TestWait.UntilAsync(() => pipeline.InFlightCount == 3);
        Assert.Equal(1, processor.StartedCount);
        Assert.Equal(1, pipeline.QueuedCount);

        hold.SetResult();
        await TestWait.UntilAsync(() => processor.CompletedCount == 3);
        await pipeline.ShutdownAsync();
    }

    [Fact]
    public async Task Maximo_de_concurrencia_no_se_excede()
    {
        var current = 0;
        var max = 0;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new RecordingProcessor(async (_, _) =>
        {
            var now = Interlocked.Increment(ref current);
            int snapshot;
            do
            {
                snapshot = Volatile.Read(ref max);
                if (now <= snapshot)
                    break;
            }
            while (Interlocked.CompareExchange(ref max, now, snapshot) != snapshot);

            try
            {
                await hold.Task;
            }
            finally
            {
                Interlocked.Decrement(ref current);
            }
        });
        var (pipeline, _) = PipelineFactory.Create(
            PipelineFactory.FastOptions(maxConcurrency: 2, queueCapacity: 10),
            processor: processor);
        pipeline.Start();

        pipeline.Submit(@"C:\DbInda\Entrada\c1.xml", CancellationToken.None);
        pipeline.Submit(@"C:\DbInda\Entrada\c2.xml", CancellationToken.None);
        pipeline.Submit(@"C:\DbInda\Entrada\c3.xml", CancellationToken.None);
        pipeline.Submit(@"C:\DbInda\Entrada\c4.xml", CancellationToken.None);

        await TestWait.UntilAsync(() => processor.StartedCount == 2);
        await Task.Delay(30);
        Assert.Equal(2, max);
        Assert.Equal(2, processor.StartedCount);

        hold.SetResult();
        await TestWait.UntilAsync(() => processor.CompletedCount == 4);
        await pipeline.ShutdownAsync();
    }

    [Fact]
    public async Task Excepcion_en_un_fichero_no_mata_a_los_demas()
    {
        var processor = new RecordingProcessor((path, _) =>
        {
            if (path.Contains("malo", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("fallo aislado");
            return Task.CompletedTask;
        });
        var (pipeline, _) = PipelineFactory.Create(processor: processor);
        pipeline.Start();

        pipeline.Submit(@"C:\DbInda\Entrada\malo.xml", CancellationToken.None);
        pipeline.Submit(@"C:\DbInda\Entrada\bueno.xml", CancellationToken.None);

        await TestWait.UntilAsync(() => processor.StartedCount == 2);
        await TestWait.UntilAsync(() => processor.CompletedCount == 1);
        await TestWait.UntilAsync(() => pipeline.InFlightCount == 0);
        Assert.Contains(processor.CompletedPaths, p => p.Contains("bueno", StringComparison.OrdinalIgnoreCase));

        pipeline.Submit(@"C:\DbInda\Entrada\despues.xml", CancellationToken.None);
        await TestWait.UntilAsync(() => processor.CompletedCount == 2);

        await pipeline.ShutdownAsync();
    }

    [Fact]
    public async Task Cancelacion_deja_de_aceptar_trabajo_nuevo_y_no_procesa_el_fichero_en_readiness()
    {
        var holdReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new WaitThenReadyProbe(holdReady.Task);
        var (pipeline, processor) = PipelineFactory.Create(
            PipelineFactory.FastOptions(stableChecks: 1),
            probe);
        using var cts = new CancellationTokenSource();
        pipeline.Start();
        pipeline.Submit(@"C:\DbInda\Entrada\pendiente.xml", cts.Token);

        await TestWait.UntilAsync(() => probe.Waiting);
        cts.Cancel();
        holdReady.SetResult();

        await pipeline.ShutdownAsync();
        Assert.Equal(0, processor.StartedCount);
    }

    [Fact]
    public async Task Ruta_vuelve_a_ser_elegible_al_terminar()
    {
        var (pipeline, processor) = PipelineFactory.Create();
        pipeline.Start();
        const string path = @"C:\DbInda\Entrada\reintento.xml";

        pipeline.Submit(path, CancellationToken.None);
        await TestWait.UntilAsync(() => processor.CompletedCount == 1);
        await TestWait.UntilAsync(() => pipeline.InFlightCount == 0);

        pipeline.Submit(path, CancellationToken.None);
        await TestWait.UntilAsync(() => processor.CompletedCount == 2);

        await pipeline.ShutdownAsync();
    }

    private sealed class WaitThenReadyProbe : IFileStabilityProbe
    {
        private readonly Task _release;

        public WaitThenReadyProbe(Task release) => _release = release;

        public bool Waiting { get; private set; }

        public bool Exists(string path) => true;

        public bool TryObserve(string path, out long length, out DateTime lastWriteUtc)
        {
            Waiting = true;
            if (!_release.IsCompleted)
            {
                length = 0;
                lastWriteUtc = default;
                return false;
            }

            length = 1;
            lastWriteUtc = new DateTime(2026, 8, 15, 10, 7, 59, DateTimeKind.Utc);
            return true;
        }
    }
}
