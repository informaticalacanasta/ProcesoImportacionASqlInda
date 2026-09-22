using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbInda.Tests.Inbound;
using DbInda.Worker.Configuration;
using DbInda.Worker.Files;
using DbInda.Worker.Inbound;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DbInda.Tests.Files;

public sealed class OrganizationCoordinationTests
{
    [Fact]
    public async Task Archivo_estable_no_repite_la_espera_completa()
    {
        using var root = new TempFolder();
        var probe = new CountingProbe(stableFor: int.MaxValue);
        root.WriteXml("80052_CLIENTES.TXT", "a");
        await Create(root, probe: probe, stableChecks: 3).ScanAsync(default);
        Assert.Equal("a", File.ReadAllText(root.Xml("inbox/80052_CLIENTES.TXT")));
        Assert.Equal(5, probe.ObserveCount);
    }

    [Fact]
    public async Task Cambio_tras_readiness_no_usa_la_observacion_antigua()
    {
        using var root = new TempFolder();
        var probe = new CountingProbe(stableFor: 3);
        var source = root.WriteXml("80052_CLIENTES.TXT", "a");
        await Create(root, probe: probe, stableChecks: 3, readinessTimeoutSeconds: 2).ScanAsync(default);
        Assert.True(File.Exists(source));
        Assert.Equal(4, probe.ObserveCount);
    }

    [Fact]
    public async Task Recuperacion_vuelve_a_comprobar_estabilidad()
    {
        using var root = new TempFolder();
        var source = root.WriteXml("80052_CLIENTES.TXT", "a");
        Directory.CreateDirectory(root.Xml("inbox"));
        new OrganizationJournal(root.Xml("inbox/.organizacion")).Save(new OrganizationEntry
        {
            Kind = "Txt", Prefix = "80052", Source = source, Destination = root.Xml("inbox/80052_CLIENTES.TXT"), Hash = Hash("a")
        });
        var probe = new CountingProbe(stableFor: int.MaxValue);
        await Create(root, probe: probe, stableChecks: 3).ScanAsync(default);
        Assert.Equal("a", File.ReadAllText(root.Xml("inbox/80052_CLIENTES.TXT")));
        Assert.Equal(3, probe.ObserveCount);
    }

    [Fact]
    public async Task Archivo_bloqueado_no_detiene_otro_prefijo()
    {
        using var root = new TempFolder();
        var blocked = root.WriteXml("111_DATOS.TXT", "bloqueado");
        root.WriteXml("bnd111", "");
        root.WriteXml("222_DATOS.TXT", "libre");
        root.WriteXml("bnd222", "");
        using var held = new FileStream(blocked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Create(root, readinessTimeoutSeconds: 1, time: TimeProvider.System, maxConcurrency: 2).ScanAsync(default);
        Assert.True(File.Exists(blocked));
        Assert.True(File.Exists(root.Xml("bnd111")));
        Assert.Equal("libre", File.ReadAllText(root.Xml("inboxOrganizado/DATOS.TXT")));
    }

    [Fact]
    public async Task La_concurrencia_no_supera_el_maximo_y_las_esperas_se_solapan()
    {
        using var root = new TempFolder();
        var probe = new GateProbe(releaseAt: 2);
        for (var i = 0; i < 4; i++)
            root.WriteXml($"8005{i}_CLIENTE.TXT", "a" + i);
        await Create(root, probe: probe, maxConcurrency: 2).ScanAsync(default);
        Assert.Equal(2, probe.Peak);
        Assert.Equal(4, Directory.GetFiles(root.Xml("inbox"), "*CLIENTE.TXT").Length);
    }

    [Fact]
    public async Task Una_ruta_no_se_observa_dos_veces_a_la_vez()
    {
        using var root = new TempFolder();
        var probe = new ReentryProbe();
        root.WriteXml("80052_CLIENTES.TXT", "a");
        await Create(root, probe: probe, maxConcurrency: 4).ScanAsync(default);
        Assert.False(probe.Reentered);
        Assert.Equal("a", File.ReadAllText(root.Xml("inbox/80052_CLIENTES.TXT")));
    }

    [Fact]
    public async Task Una_excepcion_no_detiene_el_resto()
    {
        using var root = new TempFolder();
        root.WriteXml("80052_MALO.TXT", "malo");
        root.WriteXml("80053_BUENO.TXT", "bueno");
        await Create(root, probe: new ThrowingProbe("MALO"), maxConcurrency: 2).ScanAsync(default);
        Assert.True(File.Exists(root.Xml("80052_MALO.TXT")));
        Assert.Equal("bueno", File.ReadAllText(root.Xml("inbox/80053_BUENO.TXT")));
    }

    [Fact]
    public async Task Los_PDF_no_esperan_a_que_terminen_todos_los_TXT()
    {
        using var root = new TempFolder();
        var probe = new HoldTxtUntilPdfProbe();
        for (var i = 0; i < 4; i++)
            root.WriteXml($"7000{i}_DATOS.TXT", "t" + i);
        var directory = Directory.CreateDirectory(root.Xml("procesados/2026/09/22")).FullName;
        var xml = Path.Combine(directory, "fact_sin_firmar.xml");
        File.WriteAllText(xml, "xml");
        root.WriteXml("fact_sin_firmar.pdf", "pdf");
        var lookup = new CountingLookup { Paths = [xml], OnCall = () => probe.PdfStarted.Set() };
        await Create(root, probe: probe, lookup: lookup, maxConcurrency: 2).ScanAsync(default);
        Assert.True(probe.Overlapped);
        Assert.Equal("pdf", File.ReadAllText(Path.Combine(directory, "fact_sin_firmar.pdf")));
    }

    [Fact]
    public async Task La_cancelacion_durante_la_espera_se_recupera_en_la_pasada_siguiente()
    {
        using var root = new TempFolder();
        var source = root.WriteXml("80052_CLIENTES.TXT", "a");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var blocked = Create(root, probe: new NeverReadyProbe(), readinessTimeoutSeconds: 30, time: TimeProvider.System);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked.ScanAsync(cancellation.Token));
        Assert.True(File.Exists(source));
        await Create(root).ScanAsync(default);
        Assert.Equal("a", File.ReadAllText(root.Xml("inbox/80052_CLIENTES.TXT")));
    }

    [Fact]
    public async Task Varios_TXT_del_mismo_lote_se_autorizan_juntos_tras_reinicio()
    {
        using var root = new TempFolder();
        root.WriteXml("80052_UNO.TXT", "uno");
        root.WriteXml("80052_DOS.TXT", "dos");
        root.WriteXml("80052_TRES.TXT", "tres");
        await Create(root, maxConcurrency: 4).ScanAsync(default);
        Assert.Empty(Directory.GetFiles(root.Xml("inboxOrganizado")));
        var restart = Create(root, maxConcurrency: 4);
        root.WriteXml("bnd80052", "");
        await restart.ScanAsync(default);
        Assert.Equal("uno", File.ReadAllText(root.Xml("inboxOrganizado/UNO.TXT")));
        Assert.Equal("dos", File.ReadAllText(root.Xml("inboxOrganizado/DOS.TXT")));
        Assert.Equal("tres", File.ReadAllText(root.Xml("inboxOrganizado/TRES.TXT")));
        var marker = Assert.Single(ReadJournal(root), entry => entry.Kind == "Marker");
        Assert.Equal(3, marker.Members.Count);
    }

    [Fact]
    public async Task El_indicador_guarda_el_lote_antes_de_trasladarse()
    {
        using var root = new TempFolder();
        root.WriteXml("80052_CLIENTES.TXT", "a");
        root.WriteXml("bnd80052", "");
        await Create(root, probe: new FailMarkerOnThirdObserve(), stableChecks: 1).ScanAsync(default);
        Assert.True(File.Exists(root.Xml("bnd80052")));
        Assert.Empty(Directory.GetFiles(root.Xml("inboxOrganizado")));
        var planned = Assert.Single(new OrganizationJournal(root.Xml("inbox/.organizacion")).Load(), entry => entry.Kind == "Marker");
        Assert.Equal("Planned", planned.State);
        Assert.NotEmpty(planned.Members);
        await Create(root).ScanAsync(default);
        Assert.Equal("a", File.ReadAllText(root.Xml("inboxOrganizado/CLIENTES.TXT")));
    }

    [Fact]
    public async Task Pdf_normal_y_a4_comparten_una_consulta_y_la_ausencia_no_se_cachea()
    {
        using var root = new TempFolder();
        root.WriteXml("fact_sin_firmar.pdf", "pdf");
        root.WriteXml("fact_a4_sin_firmar.pdf", "a4");
        var lookup = new CountingLookup();
        await Create(root, lookup: lookup).ScanAsync(default);
        Assert.Equal(1, lookup.Calls);
        Assert.True(File.Exists(root.Xml("fact_sin_firmar.pdf")));
        await Create(root, lookup: lookup).ScanAsync(default);
        Assert.Equal(2, lookup.Calls);
    }

    [Fact]
    public async Task Sql_no_disponible_consulta_una_vez_y_no_detiene_el_TXT()
    {
        using var root = new TempFolder();
        root.WriteXml("fact_sin_firmar.pdf", "pdf");
        root.WriteXml("fact_a4_sin_firmar.pdf", "a4");
        root.WriteXml("80052_CLIENTES.TXT", "a");
        root.WriteXml("bnd80052", "");
        var lookup = new CountingLookup { Fail = true };
        await Create(root, lookup: lookup, maxConcurrency: 4).ScanAsync(default);
        Assert.Equal(1, lookup.Calls);
        Assert.True(File.Exists(root.Xml("fact_sin_firmar.pdf")));
        Assert.True(File.Exists(root.Xml("fact_a4_sin_firmar.pdf")));
        Assert.Equal("a", File.ReadAllText(root.Xml("inboxOrganizado/CLIENTES.TXT")));
    }

    [Fact]
    public async Task Colision_sin_cambios_no_reescribe_el_registro_y_se_reintenta_si_queda_libre()
    {
        using var root = new TempFolder();
        root.WriteXml("80052_CLIENTES.TXT", "a");
        root.WriteXml("bnd80052", "");
        await Create(root).ScanAsync(default);
        root.WriteXml("80053_CLIENTES.TXT", "b");
        root.WriteXml("bnd80053", "");
        await Create(root).ScanAsync(default);
        var journalDir = root.Xml("inbox/.organizacion");
        var before = Directory.GetFiles(journalDir, "*.json").ToDictionary(path => path, File.GetLastWriteTimeUtc);
        Assert.NotEmpty(before);
        await Create(root).ScanAsync(default);
        foreach (var (path, written) in before)
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Equal("a", File.ReadAllText(root.Xml("inboxOrganizado/CLIENTES.TXT")));
        File.Delete(root.Xml("inboxOrganizado/CLIENTES.TXT"));
        await Create(root).ScanAsync(default);
        Assert.Equal("b", File.ReadAllText(root.Xml("inboxOrganizado/CLIENTES.TXT")));
    }

    [Fact]
    public void Escrituras_concurrentes_no_corrompen_el_registro()
    {
        using var root = new TempFolder();
        var journal = new OrganizationJournal(root.Xml("inbox/.organizacion"));
        var ids = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        Parallel.ForEach(ids, id => journal.Save(new OrganizationEntry
        {
            Id = id, Kind = "Txt", Prefix = "1", Source = id, Destination = id, Hash = id
        }));
        var loaded = journal.Load().ToDictionary(entry => entry.Id);
        Assert.Equal(ids.Length, loaded.Count);
        Assert.All(ids, id => Assert.Equal(id, loaded[id].Hash));
    }

    [Fact]
    public void Los_limites_del_organizador_tienen_valores_iniciales()
    {
        var options = new OrganizationOptions();
        Assert.Equal(4, options.MaxConcurrency);
        Assert.Equal(2, options.ScanIntervalSeconds);
        Assert.True(options.Enabled);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static IEnumerable<OrganizationEntry> ReadJournal(TempFolder root) =>
        Directory.GetFiles(root.Xml("inbox/.organizacion"), "*.json", SearchOption.AllDirectories)
            .Select(path => JsonSerializer.Deserialize<OrganizationEntry>(File.ReadAllText(path))!);

    private static ReceivedFileOrganizer Create(TempFolder root, IFileStabilityProbe? probe = null, CountingLookup? lookup = null,
        int maxConcurrency = 4, int stableChecks = 1, int readinessTimeoutSeconds = 10, TimeProvider? time = null)
    {
        var readiness = new FileReadinessChecker(
            Options.Create(PipelineFactory.FastOptions(stableChecks: stableChecks)),
            time ?? new ImmediateTimeProvider(),
            probe ?? new FileSystemStabilityProbe(),
            NullLogger<FileReadinessChecker>.Instance);
        return new ReceivedFileOrganizer(
            Options.Create(new PathsOptions { Input = root.Path }),
            Options.Create(new OrganizationOptions { MaxConcurrency = maxConcurrency, ReadinessTimeoutSeconds = readinessTimeoutSeconds }),
            readiness, lookup ?? new CountingLookup(), NullLogger<ReceivedFileOrganizer>.Instance);
    }

    private sealed class CountingProbe(int stableFor) : IFileStabilityProbe
    {
        private int _count;
        private static readonly DateTime First = new(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Later = First.AddMinutes(1);
        public int ObserveCount => _count;
        public bool Exists(string path) => true;
        public bool TryObserve(string path, out long length, out DateTime lastWriteUtc)
        {
            var n = Interlocked.Increment(ref _count);
            var stable = n <= stableFor;
            length = stable ? 10 : 99;
            lastWriteUtc = stable ? First : Later;
            return true;
        }
    }

    private sealed class GateProbe(int releaseAt) : IFileStabilityProbe
    {
        private int _inside;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Peak;
        public bool Exists(string path) => true;
        public bool TryObserve(string path, out long length, out DateTime lastWriteUtc)
        {
            length = 8;
            lastWriteUtc = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
            var now = Interlocked.Increment(ref _inside);
            int seen;
            do
            {
                seen = Peak;
                if (now <= seen) break;
            }
            while (Interlocked.CompareExchange(ref Peak, now, seen) != seen);
            if (now >= releaseAt)
                _release.TrySetResult();
            if (!_release.Task.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Las esperas no llegaron a solaparse.");
            Interlocked.Decrement(ref _inside);
            return true;
        }
    }

    private sealed class ReentryProbe : IFileStabilityProbe
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _inside = new(StringComparer.OrdinalIgnoreCase);
        public bool Reentered { get; private set; }
        public bool Exists(string path) => true;
        public bool TryObserve(string path, out long length, out DateTime lastWriteUtc)
        {
            length = 4;
            lastWriteUtc = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
            lock (_gate)
            {
                if (!_inside.Add(path))
                    Reentered = true;
            }
            Thread.Sleep(30);
            lock (_gate)
                _inside.Remove(path);
            return true;
        }
    }

    private sealed class ThrowingProbe(string namePart) : IFileStabilityProbe
    {
        public bool Exists(string path) => true;
        public bool TryObserve(string path, out long length, out DateTime lastWriteUtc)
        {
            length = 1;
            lastWriteUtc = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
            if (path.Contains(namePart, StringComparison.OrdinalIgnoreCase))
                throw new IOException("fallo aislado");
            return true;
        }
    }

    private sealed class HoldTxtUntilPdfProbe : IFileStabilityProbe
    {
        private readonly object _countsGate = new();
        private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
        public ManualResetEventSlim PdfStarted { get; } = new(false);
        public bool Overlapped { get; private set; }
        public bool Exists(string path) => true;
        public bool TryObserve(string path, out long length, out DateTime lastWriteUtc)
        {
            length = 2;
            lastWriteUtc = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
            if (!path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                return true;
            lock (_countsGate)
            {
                _counts.TryGetValue(path, out var seen);
                _counts[path] = seen + 1;
                if (seen > 0)
                    return true;
            }
            if (PdfStarted.Wait(TimeSpan.FromSeconds(3)))
                Overlapped = true;
            else
                throw new TimeoutException("El PDF no empezó mientras un TXT seguía en espera.");
            return true;
        }
    }

    private sealed class NeverReadyProbe : IFileStabilityProbe
    {
        public bool Exists(string path) => true;
        public bool TryObserve(string path, out long length, out DateTime lastWriteUtc)
        {
            length = 0;
            lastWriteUtc = default;
            return false;
        }
    }

    private sealed class FailMarkerOnThirdObserve : IFileStabilityProbe
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
        public bool Exists(string path) => true;
        public bool TryObserve(string path, out long length, out DateTime lastWriteUtc)
        {
            int seen;
            lock (_gate)
            {
                _counts.TryGetValue(path, out seen);
                _counts[path] = seen + 1;
            }
            var marker = Path.GetFileName(path).StartsWith("bnd", StringComparison.OrdinalIgnoreCase);
            length = marker && seen >= 2 ? 50 : 5;
            lastWriteUtc = marker && seen >= 2
                ? new DateTime(2026, 9, 22, 1, 0, 0, DateTimeKind.Utc)
                : new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
            return true;
        }
    }

    private sealed class CountingLookup : IArchivedInvoiceLookup
    {
        private int _calls;
        public int Calls => _calls;
        public bool Fail { get; set; }
        public IReadOnlyList<string> Paths { get; set; } = [];
        public Action? OnCall { get; set; }
        public Task<IReadOnlyList<string>> FindAsync(string originXmlPath, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            OnCall?.Invoke();
            if (Fail)
                throw new IOException("Consulta SQL no disponible en el test");
            return Task.FromResult(Paths);
        }
    }
}
