using DbInda.Tests.Inbound;
using DbInda.Worker.Configuration;
using DbInda.Worker.Files;
using DbInda.Worker.Inbound;
using DbInda.Worker.Orders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DbInda.Tests.Orders;

public sealed class PedidoFileMirrorTests
{
    [Fact]
    public async Task El_original_permanece_y_un_segundo_ciclo_no_duplica()
    {
        using var root = new TempFolder();
        var harness = Harness.Create(root);
        var inbox = harness.WriteInbox(PedidoSamples.Prefixed, PedidoSamples.File());
        await harness.Mirror.ScanAsync(default);

        var pending = Assert.Single(Directory.GetFiles(harness.Pending));
        Assert.Equal(PedidoSamples.Name, Path.GetFileName(pending));
        Assert.Equal(PedidoSamples.File(), File.ReadAllText(inbox));
        Assert.Empty(Directory.GetFiles(harness.Staging));
        var hash = Sha256FileHasher.ComputeHex(inbox);
        Assert.Equal(PedidoMirrorStates.Mirrored, harness.Journal.Find(hash)!.State);

        File.Delete(pending);
        await harness.Mirror.ScanAsync(default);
        Assert.Empty(Directory.GetFiles(harness.Pending));
        Assert.True(File.Exists(inbox));
    }

    [Fact]
    public async Task El_mismo_hash_con_otro_nombre_no_crea_otra_copia()
    {
        using var root = new TempFolder();
        var harness = Harness.Create(root);
        var contents = PedidoSamples.File();
        harness.WriteInbox(PedidoSamples.Prefixed, contents);
        harness.WriteInbox("90167_" + PedidoSamples.Name, contents);
        await harness.Mirror.ScanAsync(default);

        Assert.Single(Directory.GetFiles(harness.Pending));
        var hash = Sha256FileHasher.ComputeHex(harness.Root.Xml(Path.Combine("inbox", PedidoSamples.Prefixed)));
        Assert.Equal(2, harness.Journal.Find(hash)!.Sources.Count);
    }

    [Fact]
    public async Task Otro_hash_con_el_mismo_nombre_no_sobrescribe()
    {
        using var root = new TempFolder();
        var harness = Harness.Create(root);
        var firstPath = harness.WriteInbox(PedidoSamples.Prefixed, PedidoSamples.File());
        var other = PedidoSamples.File(PedidoSamples.Line(cells => PedidoSamples.Set(cells, "QUANTITAT", "2")));
        var otherPath = harness.WriteInbox("90167_" + PedidoSamples.Name, other);
        await harness.Mirror.ScanAsync(default);

        var pendingFiles = Directory.GetFiles(harness.Pending);
        var hashes = pendingFiles.Select(Sha256FileHasher.ComputeHex).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, pendingFiles.Length);
        Assert.Contains(Sha256FileHasher.ComputeHex(firstPath), hashes);
        Assert.Contains(Sha256FileHasher.ComputeHex(otherPath), hashes);
        Assert.Contains(pendingFiles, file => Path.GetFileName(file) == PedidoSamples.Name);
    }

    [Fact]
    public async Task Preparado_con_staging_y_sin_final_se_publica_al_recuperar()
    {
        using var root = new TempFolder();
        var harness = Harness.Create(root);
        var contents = PedidoSamples.File();
        Directory.CreateDirectory(harness.Staging);
        var temp = Path.Combine(harness.Staging, "temp.txt");
        File.WriteAllText(temp, contents);
        var hash = Sha256FileHasher.ComputeHex(temp);
        var stagingName = hash + "__" + PedidoSamples.Name;
        File.Move(temp, Path.Combine(harness.Staging, stagingName));
        harness.Journal.Save(new PedidoMirrorRecord
        {
            Sha256 = hash,
            State = PedidoMirrorStates.Prepared,
            FinalName = PedidoSamples.Name,
            StagingName = stagingName
        });

        Assert.False(Directory.Exists(harness.Pending) && Directory.EnumerateFiles(harness.Pending).Any());
        await harness.Mirror.RecoverAsync(default);

        var pending = Assert.Single(Directory.GetFiles(harness.Pending));
        Assert.Equal(PedidoSamples.Name, Path.GetFileName(pending));
        Assert.Equal(contents, File.ReadAllText(pending));
        Assert.Equal(PedidoMirrorStates.Mirrored, harness.Journal.Find(hash)!.State);
        Assert.Empty(Directory.GetFiles(harness.Staging));
    }

    [Fact]
    public async Task Preparado_con_final_ya_visible_solo_cierra_el_diario()
    {
        using var root = new TempFolder();
        var harness = Harness.Create(root);
        var contents = PedidoSamples.File();
        Directory.CreateDirectory(harness.Pending);
        var pendingFile = Path.Combine(harness.Pending, PedidoSamples.Name);
        File.WriteAllText(pendingFile, contents);
        var hash = Sha256FileHasher.ComputeHex(pendingFile);
        harness.Journal.Save(new PedidoMirrorRecord
        {
            Sha256 = hash,
            State = PedidoMirrorStates.Prepared,
            FinalName = PedidoSamples.Name,
            StagingName = hash + "__" + PedidoSamples.Name
        });

        await harness.Mirror.RecoverAsync(default);
        Assert.Single(Directory.GetFiles(harness.Pending));
        Assert.Equal(PedidoMirrorStates.Mirrored, harness.Journal.Find(hash)!.State);
    }

    [Fact]
    public async Task Preparado_y_ya_archivado_no_vuelve_a_copiar_el_inbox()
    {
        using var root = new TempFolder();
        var harness = Harness.Create(root);
        var inbox = harness.WriteInbox(PedidoSamples.Prefixed, PedidoSamples.File());
        await harness.Mirror.ScanAsync(default);
        var pending = Assert.Single(Directory.GetFiles(harness.Pending));
        var archivedDir = Path.Combine(harness.Processed, "2026", "09", "23", "167");
        Directory.CreateDirectory(archivedDir);
        var archived = Path.Combine(archivedDir, PedidoSamples.Name);
        File.Move(pending, archived);

        var hash = Sha256FileHasher.ComputeHex(inbox);
        var record = harness.Journal.Find(hash)!;
        record.State = PedidoMirrorStates.Prepared;
        record.MirroredUtc = null;
        harness.Journal.Save(record);

        await harness.Mirror.ScanAsync(default);
        Assert.Empty(Directory.GetFiles(harness.Pending));
        Assert.True(File.Exists(inbox));
        Assert.True(File.Exists(archived));
        Assert.Equal(PedidoMirrorStates.Mirrored, harness.Journal.Find(hash)!.State);
    }

    [Fact]
    public async Task Un_staging_truncado_se_descarta_y_el_inbox_se_copia_una_vez()
    {
        using var root = new TempFolder();
        var harness = Harness.Create(root);
        harness.WriteInbox(PedidoSamples.Prefixed, PedidoSamples.File());
        Directory.CreateDirectory(harness.Staging);
        File.WriteAllBytes(Path.Combine(harness.Staging, new string('A', 64) + "__" + PedidoSamples.Name), [1, 2, 3]);

        await harness.Mirror.ScanAsync(default);
        Assert.Single(Directory.GetFiles(harness.Pending));
        Assert.Empty(Directory.GetFiles(harness.Staging));
    }

    [Fact]
    public async Task Un_staging_huerfano_completo_se_adopta_sin_duplicar()
    {
        using var root = new TempFolder();
        var harness = Harness.Create(root);
        var contents = PedidoSamples.File();
        harness.WriteInbox(PedidoSamples.Prefixed, contents);
        Directory.CreateDirectory(harness.Staging);
        var temp = Path.Combine(harness.Staging, "temp.txt");
        File.WriteAllText(temp, contents);
        var hash = Sha256FileHasher.ComputeHex(temp);
        File.Move(temp, Path.Combine(harness.Staging, hash + "__" + PedidoSamples.Name));

        await harness.Mirror.ScanAsync(default);
        Assert.Single(Directory.GetFiles(harness.Pending));
        Assert.Equal(PedidoMirrorStates.Mirrored, harness.Journal.Find(hash)!.State);
    }

    [Fact]
    public async Task Un_diario_corrupto_no_se_trata_como_espejado()
    {
        using var root = new TempFolder();
        var mirrorDir = Path.Combine(root.Path, "pedidos", ".mirror");
        Directory.CreateDirectory(mirrorDir);
        File.WriteAllText(Path.Combine(mirrorDir, new string('B', 64) + ".json"), "{");
        var harness = Harness.Create(root);
        harness.WriteInbox(PedidoSamples.Prefixed, PedidoSamples.File());

        await harness.Mirror.ScanAsync(default);
        Assert.Single(Directory.GetFiles(harness.Pending));
        Assert.Contains(Directory.GetFiles(mirrorDir), file => file.EndsWith(".corrupto", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task No_espeja_lo_que_solo_esta_en_inbox_organizado()
    {
        using var root = new TempFolder();
        var harness = Harness.Create(root);
        var organized = Path.Combine(root.Path, "inboxOrganizado");
        Directory.CreateDirectory(organized);
        File.WriteAllText(Path.Combine(organized, PedidoSamples.Name), PedidoSamples.File());
        await harness.Mirror.ScanAsync(default);
        Assert.False(Directory.Exists(harness.Pending) && Directory.EnumerateFiles(harness.Pending).Any());
    }

    private sealed class Harness
    {
        public required PedidoFileMirror Mirror { get; init; }
        public required PedidoMirrorJournal Journal { get; init; }
        public required string Pending { get; init; }
        public required string Processed { get; init; }
        public required string Staging { get; init; }
        public required TempFolder Root { get; init; }

        public string WriteInbox(string name, string contents)
        {
            var path = Root.Xml(Path.Combine("inbox", name));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }

        public static Harness Create(TempFolder root)
        {
            var orders = new OrderOptions
            {
                Pending = Path.Combine(root.Path, "pedidos", "pendientes"),
                Processed = Path.Combine(root.Path, "pedidos", "procesados"),
                Errors = Path.Combine(root.Path, "pedidos", "errores"),
                MaxConcurrency = 2,
                ScanIntervalSeconds = 10
            };
            Directory.CreateDirectory(Path.Combine(root.Path, "inbox"));
            var processing = Options.Create(new ProcessingOptions
            {
                StableChecks = 1,
                StableCheckDelayMilliseconds = 1,
                MaxConcurrency = 1,
                QueueCapacity = 1,
                ScanIntervalSeconds = 1
            });
            var probe = new FileSystemStabilityProbe();
            var checker = new FileReadinessChecker(
                processing,
                TimeProvider.System,
                probe,
                NullLogger<FileReadinessChecker>.Instance);
            var journal = new PedidoMirrorJournal(PedidoLayout.Mirror(orders), NullLogger<PedidoMirrorJournal>.Instance);
            var mirror = new PedidoFileMirror(
                Options.Create(orders),
                Options.Create(new PathsOptions
                {
                    Input = root.Path,
                    Processed = Path.Combine(root.Path, "procesados"),
                    Errors = Path.Combine(root.Path, "errores"),
                    Xsd = root.Path,
                    Logs = root.Path
                }),
                Options.Create(new OrganizationOptions()),
                checker,
                probe,
                journal,
                TimeProvider.System,
                NullLogger<PedidoFileMirror>.Instance);
            return new Harness
            {
                Mirror = mirror,
                Journal = journal,
                Pending = orders.Pending,
                Processed = orders.Processed,
                Staging = PedidoLayout.Staging(orders),
                Root = root
            };
        }
    }
}
