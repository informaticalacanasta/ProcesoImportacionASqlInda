using DbInda.Worker.Inbound;

namespace DbInda.Tests.Inbound;

public sealed class FilePathNormalizerTests
{
    [Fact]
    public void Normalize_devuelve_una_ruta_absoluta()
    {
        var normalized = FilePathNormalizer.Normalize("entrada.xml");
        Assert.True(Path.IsPathRooted(normalized));
        Assert.Equal(Path.GetFullPath("entrada.xml"), normalized);
    }

    [Fact]
    public void ForIdentity_es_insensitive_solo_en_Windows()
    {
        Assert.Equal(
            OperatingSystem.IsWindows(),
            FilePathComparer.ForIdentity.Equals("Ticket.xml", "ticket.xml"));
    }
}
