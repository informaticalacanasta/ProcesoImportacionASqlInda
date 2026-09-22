using System.Security.Cryptography;
using DbInda.Tests.Inbound;
using DbInda.Worker.Configuration;
using DbInda.Worker.Files;
using DbInda.Worker.Inbound;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DbInda.Tests.Files;

public sealed class ReceivedFileOrganizerTests
{
    [Fact]
    public async Task Txt_sin_bnd_se_conserva_sin_publicar()
    {
        using var root = new TempFolder();
        root.WriteXml("80052_CLIENTES.TXT", "clientes");
        await Create(root).ScanAsync(default);
        Assert.False(File.Exists(root.Xml("80052_CLIENTES.TXT")));
        Assert.Equal("clientes", File.ReadAllText(root.Xml("inbox/80052_CLIENTES.TXT")));
        Assert.Empty(Directory.GetFiles(root.Xml("inboxOrganizado")));
    }

    [Fact]
    public async Task Bnd_autoriza_solo_prefijo_exacto_y_no_lotes_futuros()
    {
        using var root = new TempFolder();
        root.WriteXml("80052_CLIENTES.TXT", "a");
        root.WriteXml("800520_OTRO.TXT", "b");
        root.WriteXml("bnd80052", "");
        await Create(root).ScanAsync(default);
        Assert.Equal("a", File.ReadAllText(root.Xml("inboxOrganizado/CLIENTES.TXT")));
        Assert.False(File.Exists(root.Xml("inboxOrganizado/OTRO.TXT")));
        Assert.True(File.Exists(root.Xml("inbox/bnd80052")));
        root.WriteXml("80052_NUEVO.TXT", "nuevo");
        await Create(root).ScanAsync(default); // restart, no new marker
        Assert.False(File.Exists(root.Xml("inboxOrganizado/NUEVO.TXT")));
        root.WriteXml("bnd80052", "");
        await Create(root).ScanAsync(default);
        Assert.True(File.Exists(root.Xml("inboxOrganizado/NUEVO.TXT")));
        Assert.Single(Directory.GetFiles(root.Xml("inbox"), "bnd80052_REPETIDO_*"));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("nuevo")]
    public async Task Colision_nunca_sobrescribe_incluso_con_contenido_identico(string contents)
    {
        using var root = new TempFolder();
        root.WriteXml("80052_CLIENTES.TXT", "a");
        root.WriteXml("bnd80052", "");
        await Create(root).ScanAsync(default);
        root.WriteXml("80052_CLIENTES.TXT", contents);
        root.WriteXml("bnd80052", "");
        await Create(root).ScanAsync(default);
        Assert.Equal("a", File.ReadAllText(root.Xml("inboxOrganizado/CLIENTES.TXT")));
        var repeated = Assert.Single(Directory.GetFiles(root.Xml("inbox"), "80052_CLIENTES_REPETIDO_*.TXT"));
        Assert.Equal(contents, File.ReadAllText(repeated));
        Assert.Contains(Journal(root).Load(), e => e.State == "Collision");
    }

    [Fact]
    public async Task Reinicio_no_republica_txt_completado()
    {
        using var root = new TempFolder();
        root.WriteXml("80052_CLIENTES.TXT", "a");
        root.WriteXml("bnd80052", "");
        await Create(root).ScanAsync(default);
        File.Delete(root.Xml("inboxOrganizado/CLIENTES.TXT"));
        await Create(root).ScanAsync(default);
        Assert.False(File.Exists(root.Xml("inboxOrganizado/CLIENTES.TXT")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recupera_movimiento_sin_borrar_una_nueva_llegada(bool newArrival)
    {
        using var root = new TempFolder();
        Directory.CreateDirectory(root.Xml("inbox"));
        var entry = new OrganizationEntry { Kind = "Txt", Prefix = "80052", Source = root.Xml("80052_CLIENTES.TXT"),
            Destination = root.Xml("inbox/80052_CLIENTES.TXT"), Hash = Hash("a") };
        File.WriteAllText(entry.Destination, "a");
        Journal(root).Save(entry);
        if (newArrival) root.WriteXml("80052_CLIENTES.TXT", "b");
        await Create(root).ScanAsync(default);
        Assert.Equal("a", File.ReadAllText(entry.Destination));
        if (newArrival)
            Assert.Equal("b", File.ReadAllText(Assert.Single(Directory.GetFiles(root.Xml("inbox"), "80052_CLIENTES_REPETIDO_*"))));
    }

    [Fact]
    public async Task Recupera_intencion_antes_del_movimiento()
    {
        using var root = new TempFolder();
        var source = root.WriteXml("80052_CLIENTES.TXT", "a");
        Journal(root).Save(new OrganizationEntry { Kind = "Txt", Prefix = "80052", Source = source,
            Destination = root.Xml("inbox/80052_CLIENTES.TXT"), Hash = Hash("a") });
        await Create(root).ScanAsync(default);
        Assert.False(File.Exists(source));
        Assert.Equal("a", File.ReadAllText(root.Xml("inbox/80052_CLIENTES.TXT")));
    }

    [Fact]
    public async Task Un_error_de_recuperacion_no_detiene_otro_lote()
    {
        using var root = new TempFolder();
        Journal(root).Save(new OrganizationEntry { Kind = "Txt", Source = root.Xml("missing"), Destination = root.Xml("inbox/missing"), Hash = Hash("a") });
        root.WriteXml("80052_CLIENTES.TXT", "a");
        root.WriteXml("bnd80052", "");
        await Create(root).ScanAsync(default);
        Assert.True(File.Exists(root.Xml("inboxOrganizado/CLIENTES.TXT")));
    }

    [Fact]
    public async Task Recupera_publicacion_tras_rename_antes_de_guardar_estado()
    {
        using var root = new TempFolder();
        Directory.CreateDirectory(root.Xml("inboxOrganizado"));
        Directory.CreateDirectory(root.Xml("inbox"));
        var entry = new OrganizationEntry { Kind = "Txt", Prefix = "80052", Source = root.Xml("80052_CLIENTES.TXT"),
            Destination = root.Xml("inbox/80052_CLIENTES.TXT"), Hash = Hash("a"), State = "Publishing",
            CopyDestination = root.Xml("inboxOrganizado/CLIENTES.TXT") };
        File.WriteAllText(entry.Destination, "a");
        File.WriteAllText(entry.CopyDestination, "a");
        Journal(root).Save(entry);
        Journal(root).Save(new OrganizationEntry { Kind = "Marker", State = "Staged", Members = [entry.Id] });
        await Create(root).ScanAsync(default);
        Assert.True(Journal(root).IsRetired(entry.Id));
    }

    [Fact]
    public async Task Pdf_tardios_se_archivan_junto_al_xml_y_conservan_bytes()
    {
        using var root = new TempFolder();
        var lookup = new Lookup();
        var normal = root.WriteXml("fact_sin_firmar.pdf", "pdf");
        var a4 = root.WriteXml("fact_a4_sin_firmar.pdf", "a4");
        await Create(root, lookup).ScanAsync(default);
        Assert.True(File.Exists(normal));
        var dir = Directory.CreateDirectory(root.Xml("procesados/2026/07/27")).FullName;
        var xml = Path.Combine(dir, "fact_sin_firmar_R17.xml");
        File.WriteAllText(xml, "xml");
        lookup.Paths = [xml];
        await Create(root, lookup).ScanAsync(default);
        Assert.False(File.Exists(normal));
        Assert.False(File.Exists(a4));
        Assert.Equal("pdf", File.ReadAllText(Path.Combine(dir, "fact_sin_firmar_R17.pdf")));
        Assert.Equal("a4", File.ReadAllText(Path.Combine(dir, "fact_a4_sin_firmar_R17.pdf")));
        Assert.All(lookup.Requested, p => Assert.Equal(root.Xml("fact_sin_firmar.xml"), p));
    }

    [Fact]
    public async Task Pdf_ambiguo_permanece_en_entrada()
    {
        using var root = new TempFolder();
        var source = root.WriteXml("fact_sin_firmar.pdf", "pdf");
        await Create(root, new Lookup { Paths = ["one.xml", "two.xml"] }).ScanAsync(default);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Carpetas_ajenas_y_xml_no_se_modifican()
    {
        using var root = new TempFolder();
        Directory.CreateDirectory(root.Xml("verifactu"));
        var paths = new[] { "ticket.xml", "verifactu/80052_CLIENTES.TXT", "verifactu/worker.log" };
        foreach (var p in paths) root.WriteXml(p, "original");
        await Create(root).ScanAsync(default);
        foreach (var p in paths) Assert.Equal("original", File.ReadAllText(root.Xml(p)));
    }

    [Fact]
    public async Task Log_de_entrada_va_a_logs_y_conserva_bytes()
    {
        using var root = new TempFolder();
        root.WriteXml("30052_tpvision_4_151_1_2026_05-51-27.log", "linea");
        await Create(root).ScanAsync(default);
        Assert.False(File.Exists(root.Xml("30052_tpvision_4_151_1_2026_05-51-27.log")));
        Assert.Equal("linea", File.ReadAllText(root.Xml("logs/30052_tpvision_4_151_1_2026_05-51-27.log")));
    }

    [Fact]
    public async Task Log_repetido_conserva_ambas_copias()
    {
        using var root = new TempFolder();
        root.WriteXml("tpv.log", "primero");
        await Create(root).ScanAsync(default);
        root.WriteXml("tpv.log", "segundo");
        await Create(root).ScanAsync(default);
        Assert.Equal("primero", File.ReadAllText(root.Xml("logs/tpv.log")));
        var repeated = Assert.Single(Directory.GetFiles(root.Xml("logs"), "tpv_REPETIDO_*.log"));
        Assert.Equal("segundo", File.ReadAllText(repeated));
    }

    [Fact]
    public async Task Cancelacion_conserva_llegadas()
    {
        using var root = new TempFolder();
        var path = root.WriteXml("80052_CLIENTES.TXT", "a");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(root).ScanAsync(cancellation.Token));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Pdf_repetido_conserva_ambas_copias()
    {
        using var root = new TempFolder();
        var directory = Directory.CreateDirectory(root.Xml("procesados/2026/07/27")).FullName;
        var xml = Path.Combine(directory, "fact_sin_firmar.xml");
        File.WriteAllText(xml, "xml");
        var lookup = new Lookup { Paths = [xml] };
        root.WriteXml("fact_sin_firmar.pdf", "primero");
        await Create(root, lookup).ScanAsync(default);
        root.WriteXml("fact_sin_firmar.pdf", "segundo");
        await Create(root, lookup).ScanAsync(default);
        Assert.Equal("primero", File.ReadAllText(Path.Combine(directory, "fact_sin_firmar.pdf")));
        var repeated = Assert.Single(Directory.GetFiles(directory, "fact_sin_firmar_REPETIDO_*.pdf"));
        Assert.Equal("segundo", File.ReadAllText(repeated));
    }

    [Fact]
    public async Task Sql_caido_deja_pdf_pendiente_y_no_impide_txt()
    {
        using var root = new TempFolder();
        var pdf = root.WriteXml("fact_sin_firmar.pdf", "pdf");
        root.WriteXml("80052_CLIENTES.TXT", "a");
        root.WriteXml("bnd80052", "");
        await Create(root, new Lookup { Fail = true }).ScanAsync(default);
        Assert.True(File.Exists(pdf));
        Assert.Equal("a", File.ReadAllText(root.Xml("inboxOrganizado/CLIENTES.TXT")));
    }

    [Fact]
    public async Task Txt_inestable_retiene_el_marcador_y_no_publica()
    {
        using var root = new TempFolder();
        var txt = root.WriteXml("80052_CLIENTES.TXT", "a");
        var marker = root.WriteXml("bnd80052", "");
        using (var held = new FileStream(txt, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var readiness = new FileReadinessChecker(Options.Create(PipelineFactory.FastOptions()), TimeProvider.System,
                new FileSystemStabilityProbe(), NullLogger<FileReadinessChecker>.Instance);
            var organizer = new ReceivedFileOrganizer(Options.Create(new PathsOptions { Input = root.Path }),
                Options.Create(new OrganizationOptions { ReadinessTimeoutSeconds = 1 }), readiness, new Lookup(),
                NullLogger<ReceivedFileOrganizer>.Instance);
            await organizer.ScanAsync(default);
            Assert.True(File.Exists(txt));
            Assert.True(File.Exists(marker));
            Assert.Empty(Directory.GetFiles(root.Xml("inboxOrganizado")));
        }
        await Create(root).ScanAsync(default);
        Assert.True(File.Exists(root.Xml("inboxOrganizado/CLIENTES.TXT")));
    }

    [Fact]
    public void Carpetas_de_organizacion_no_pueden_ser_la_entrada()
    {
        using var root = new TempFolder();
        var readiness = new FileReadinessChecker(Options.Create(PipelineFactory.FastOptions()), TimeProvider.System,
            new FileSystemStabilityProbe(), NullLogger<FileReadinessChecker>.Instance);
        Assert.Throws<ArgumentException>(() => new ReceivedFileOrganizer(Options.Create(new PathsOptions { Input = root.Path }),
            Options.Create(new OrganizationOptions { Inbox = root.Path }), readiness, new Lookup(),
            NullLogger<ReceivedFileOrganizer>.Instance));
    }

    private static OrganizationJournal Journal(TempFolder root) => new(root.Xml("inbox/.organizacion"));
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    private static ReceivedFileOrganizer Create(TempFolder root, Lookup? lookup = null)
    {
        var readiness = new FileReadinessChecker(Options.Create(PipelineFactory.FastOptions()), new ImmediateTimeProvider(),
            new FileSystemStabilityProbe(), NullLogger<FileReadinessChecker>.Instance);
        return new(Options.Create(new PathsOptions { Input = root.Path }), Options.Create(new OrganizationOptions()),
            readiness, lookup ?? new Lookup(), NullLogger<ReceivedFileOrganizer>.Instance);
    }

    private sealed class Lookup : IArchivedInvoiceLookup
    {
        public bool Fail { get; set; }
        public IReadOnlyList<string> Paths { get; set; } = [];
        public List<string> Requested { get; } = [];
        public Task<IReadOnlyList<string>> FindAsync(string originXmlPath, CancellationToken cancellationToken)
        {
            if (Fail) throw new IOException("Consulta SQL no disponible en el test");
            Requested.Add(originXmlPath);
            return Task.FromResult(Paths);
        }
    }
}