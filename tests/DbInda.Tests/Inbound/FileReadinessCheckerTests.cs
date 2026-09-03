using DbInda.Worker.Inbound;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DbInda.Tests.Inbound;

public sealed class FileReadinessCheckerTests
{
    [Fact]
    public async Task Fichero_estable_se_acepta_tras_observaciones_iguales()
    {
        using var folder = new TempFolder();
        var path = folder.WriteXml("estable.xml");
        var checker = new FileReadinessChecker(
            Options.Create(PipelineFactory.FastOptions(stableChecks: 2)),
            new ImmediateTimeProvider(),
            new FileSystemStabilityProbe(),
            NullLogger<FileReadinessChecker>.Instance);

        Assert.True(await checker.WaitUntilReadyAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task Observaciones_iguales_conservan_tamano_y_LastWriteTimeUtc()
    {
        var utc = new DateTime(2026, 8, 15, 10, 7, 59, DateTimeKind.Utc);
        var probe = new ScriptedFileProbe
        {
            Script =
            [
                (true, 128, utc),
                (true, 128, utc)
            ]
        };

        Assert.True(await Create(probe, stableChecks: 2).WaitUntilReadyAsync(@"C:\dbinda\a.xml", CancellationToken.None));
        Assert.Equal(2, probe.ObserveCount);
    }

    [Fact]
    public async Task Fichero_creciendo_no_esta_estable_hasta_que_deja_de_cambiar()
    {
        var t1 = new DateTime(2026, 8, 15, 10, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddSeconds(1);
        var probe = new ScriptedFileProbe
        {
            Script =
            [
                (true, 10, t1),
                (true, 40, t2),
                (true, 40, t2),
                (true, 40, t2)
            ]
        };

        Assert.True(await Create(probe, stableChecks: 3).WaitUntilReadyAsync(@"C:\dbinda\creciendo.xml", CancellationToken.None));
        Assert.Equal(4, probe.ObserveCount);
    }

    [Fact]
    public async Task Fichero_que_no_estabiliza_no_se_acepta()
    {
        var t = new DateTime(2026, 8, 15, 10, 0, 0, DateTimeKind.Utc);
        var probe = new ScriptedFileProbe
        {
            Script =
            [
                (true, 1, t),
                (true, 2, t.AddSeconds(1)),
                (true, 3, t.AddSeconds(2))
            ]
        };

        Assert.False(await Create(probe, stableChecks: 3).WaitUntilReadyAsync(@"C:\dbinda\inestable.xml", CancellationToken.None));
    }

    [Fact]
    public async Task Fichero_bloqueado_no_esta_estable_y_al_cancelar_queda_no_listo()
    {
        var probe = new LockedProbe();
        var checker = Create(probe, stableChecks: 2);
        using var cts = new CancellationTokenSource();
        var wait = checker.WaitUntilReadyAsync(@"C:\dbinda\bloqueado.xml", cts.Token);
        cts.Cancel();

        Assert.False(await wait);
    }

    private static FileReadinessChecker Create(IFileStabilityProbe probe, int stableChecks)
        => new(
            Options.Create(PipelineFactory.FastOptions(stableChecks: stableChecks)),
            new ImmediateTimeProvider(),
            probe,
            NullLogger<FileReadinessChecker>.Instance);

    private sealed class LockedProbe : IFileStabilityProbe
    {
        public bool Exists(string path) => true;

        public bool TryObserve(string path, out long length, out DateTime lastWriteUtc)
        {
            length = 0;
            lastWriteUtc = default;
            return false;
        }
    }
}
