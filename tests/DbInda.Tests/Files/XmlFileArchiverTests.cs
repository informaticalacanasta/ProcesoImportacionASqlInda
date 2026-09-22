using System.Text;
using DbInda.Tests.Inbound;
using DbInda.Tests.Parsing;
using DbInda.Worker.Configuration;
using DbInda.Worker.Files;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DbInda.Tests.Files;

public sealed class XmlFileArchiverTests
{
    [Fact]
    public async Task Conserva_bytes_y_usa_estructura_de_procesados()
    {
        using var root = new TempFolder();
        var source = root.WriteXml("ticket.xml", "<a>1</a>");
        var original = File.ReadAllBytes(source);
        var hash = Sha256Of(original);
        var processed = Directory.CreateDirectory(Path.Combine(root.Path, "proc")).FullName;
        var errors = Directory.CreateDirectory(Path.Combine(root.Path, "err")).FullName;
        var archiver = Create(processed, errors);

        var dest = await ArchiveAsync(
            archiver,
            new ArchiveRequest
            {
                SourcePath = source,
                HashSha256 = hash,
                Kind = ArchiveKind.Processed,
                FolderDate = new DateOnly(2026, 8, 15),
                Tienda = 52,
                ReceptionId = 100
            });

        Assert.False(File.Exists(source));
        Assert.True(File.Exists(dest));
        Assert.Equal(original, File.ReadAllBytes(dest));
        Assert.Contains(Path.Combine("2026", "08", "15"), dest);
        Assert.EndsWith("ticket.xml", dest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Colision_no_sobrescribe_y_conserva_ambos()
    {
        using var root = new TempFolder();
        var processed = Directory.CreateDirectory(Path.Combine(root.Path, "proc")).FullName;
        var destDir = Directory.CreateDirectory(Path.Combine(processed, "2026", "08", "15")).FullName;
        var occupant = Path.Combine(destDir, "ticket.xml");
        File.WriteAllText(occupant, "<other />");
        var occupantBytes = File.ReadAllBytes(occupant);

        var source = root.WriteXml("ticket.xml", "<a>nuevo</a>");
        var sourceBytes = File.ReadAllBytes(source);
        var archiver = Create(processed, Path.Combine(root.Path, "err"));

        var dest = await ArchiveAsync(
            archiver,
            new ArchiveRequest
            {
                SourcePath = source,
                HashSha256 = Sha256Of(sourceBytes),
                Kind = ArchiveKind.Processed,
                FolderDate = new DateOnly(2026, 8, 15),
                ReceptionId = 77
            });

        Assert.True(File.Exists(occupant));
        Assert.Equal(occupantBytes, File.ReadAllBytes(occupant));
        Assert.True(File.Exists(dest));
        Assert.NotEqual(occupant, dest);
        Assert.Contains("_R77", Path.GetFileName(dest), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sourceBytes, File.ReadAllBytes(dest));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task Conserva_bytes_windows1252_sin_reconvertir()
    {
        using var root = new TempFolder();
        var xml = EncodingTestXml.WithDVendedor("ANA Mª");
        var original = EncodingTestXml.ReplaceFeminineOrdinalWithWindows1252(Encoding.UTF8.GetBytes(xml));
        var source = Path.Combine(root.Path, "ticket.xml");
        File.WriteAllBytes(source, original);
        var processed = Directory.CreateDirectory(Path.Combine(root.Path, "proc")).FullName;
        var archiver = Create(processed, Path.Combine(root.Path, "err"));

        var dest = await ArchiveAsync(
            archiver,
            new ArchiveRequest
            {
                SourcePath = source,
                HashSha256 = Sha256Of(original),
                Kind = ArchiveKind.Processed,
                FolderDate = new DateOnly(2026, 8, 15),
                Tienda = 52,
                ReceptionId = 1252
            });

        Assert.False(File.Exists(source));
        Assert.Equal(original, File.ReadAllBytes(dest));
        Assert.NotEqual(Encoding.UTF8.GetBytes(xml), File.ReadAllBytes(dest));
    }

    [Fact]
    public async Task Destino_con_el_mismo_hash_conserva_copia_propia()
    {
        using var root = new TempFolder();
        var processed = Directory.CreateDirectory(Path.Combine(root.Path, "proc")).FullName;
        var destDir = Directory.CreateDirectory(Path.Combine(processed, "2026", "01", "02")).FullName;
        var xml = "<same />";
        var existing = Path.Combine(destDir, "ticket.xml");
        File.WriteAllText(existing, xml);
        var source = root.WriteXml("ticket.xml", xml);
        var hash = Sha256Of(File.ReadAllBytes(source));
        var archiver = Create(processed, Path.Combine(root.Path, "err"));

        var dest = await ArchiveAsync(
            archiver,
            new ArchiveRequest
            {
                SourcePath = source,
                HashSha256 = hash,
                Kind = ArchiveKind.Processed,
                FolderDate = new DateOnly(2026, 1, 2),
                ReceptionId = 101
            });

        Assert.True(File.Exists(existing));
        Assert.True(File.Exists(dest));
        Assert.NotEqual(existing, dest);
        Assert.Equal(xml, File.ReadAllText(existing));
        Assert.Equal(xml, File.ReadAllText(dest));
        Assert.False(File.Exists(source));
        Assert.Contains("_R101", Path.GetFileName(dest), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ArchiveAsync(IXmlFileArchiver archiver, ArchiveRequest request)
    {
        var dest = archiver.AllocateDestination(request);
        await archiver.MoveToExactAsync(request.SourcePath, dest, request.HashSha256, CancellationToken.None);
        return dest;
    }

    private static XmlFileArchiver Create(string processed, string errors)
        => new(
            Options.Create(new PathsOptions
            {
                Input = processed,
                Processed = processed,
                Errors = errors,
                Xsd = processed,
                Logs = processed
            }),
            NullLogger<XmlFileArchiver>.Instance);

    private static string Sha256Of(byte[] bytes)
        => Sha256FileHasher.ComputeHex(bytes);
}
