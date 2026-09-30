using DbInda.Worker.Configuration;
using DbInda.Worker.Inbound;
using DbInda.Worker.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DbInda.Tests.Inbound;

public sealed class TicketStagingWorkerTests
{
    [Fact]
    public async Task XML_estable_pasa_a_pendientes_sin_tocar_pdf_ni_sobrescribir_otro_XML()
    {
        using var root = new TempFolder();
        var pending = Path.Combine(root.Path, "Tickets", "Pendientes");
        Directory.CreateDirectory(pending);
        var original = root.WriteXml("nuevo.xml", "<nuevo />");
        var repeated = root.WriteXml("ocupado.xml", "<recibido />");
        File.WriteAllText(Path.Combine(pending, "ocupado.xml"), "<anterior />");
        var pdf = Path.Combine(root.Path, "nuevo.pdf");
        File.WriteAllText(pdf, "pdf");
        var options = PipelineFactory.FastOptions(stableChecks: 2);
        var readiness = new FileReadinessChecker(Options.Create(options), TimeProvider.System,
            new FileSystemStabilityProbe(), NullLogger<FileReadinessChecker>.Instance);
        using var worker = new TicketStagingWorker(
            Options.Create(new PathsOptions { Input = root.Path, TicketPending = pending }),
            Options.Create(options), readiness, NullLogger<TicketStagingWorker>.Instance);

        await worker.StageOnceAsync(CancellationToken.None);

        Assert.False(File.Exists(original));
        Assert.Equal("<nuevo />", File.ReadAllText(Path.Combine(pending, "nuevo.xml")));
        Assert.True(File.Exists(pdf));
        Assert.Equal("<recibido />", File.ReadAllText(repeated));
        Assert.Equal("<anterior />", File.ReadAllText(Path.Combine(pending, "ocupado.xml")));
    }
}
