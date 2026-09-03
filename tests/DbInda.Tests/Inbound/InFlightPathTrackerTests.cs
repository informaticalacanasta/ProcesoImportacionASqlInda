using DbInda.Worker.Inbound;

namespace DbInda.Tests.Inbound;

public sealed class InFlightPathTrackerTests
{
    [Fact]
    public void La_misma_ruta_normalizada_no_puede_reclamarse_dos_veces()
    {
        var tracker = new InFlightPathTracker();
        var path = FilePathNormalizer.Normalize(@"C:\DbInda\Entrada\a.xml");

        Assert.True(tracker.TryClaim(path));
        Assert.False(tracker.TryClaim(path));
        Assert.True(tracker.Contains(path));

        tracker.Release(path);

        Assert.False(tracker.Contains(path));
        Assert.True(tracker.TryClaim(path));
    }

    [Fact]
    public void En_Windows_la_ruta_es_case_insensitive()
    {
        var tracker = new InFlightPathTracker();
        Assert.True(tracker.TryClaim(@"C:\DbInda\Entrada\Ticket.xml"));
        Assert.False(tracker.TryClaim(@"c:\dbinda\entrada\TICKET.XML"));
    }
}
