using DbInda.Worker.Inbound;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbInda.Tests.Inbound;

public sealed class InputDiscoveryTests
{
    [Fact]
    public async Task Scanner_periodico_descubre_xml_existentes_en_el_primer_ciclo()
    {
        using var folder = new TempFolder();
        folder.WriteXml("a.xml");
        folder.WriteXml("b.xml");
        var (pipeline, processor) = PipelineFactory.Create();
        var scanner = new InputDirectoryScanner(TimeProvider.System, NullLogger<InputDirectoryScanner>.Instance);
        using var cts = new CancellationTokenSource();
        pipeline.Start();

        var run = scanner.RunAsync(
            folder.Path,
            TimeSpan.FromSeconds(30),
            path => pipeline.Submit(path, cts.Token),
            cts.Token);

        await TestWait.UntilAsync(() => processor.CompletedCount == 2);
        cts.Cancel();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
        }

        await pipeline.ShutdownAsync();
    }

    [Fact]
    public async Task FileSystemWatcher_detecta_un_xml_nuevo()
    {
        using var folder = new TempFolder();
        var (pipeline, processor) = PipelineFactory.Create();
        pipeline.Start();
        using var watcher = new InputXmlWatcher(NullLogger<InputXmlWatcher>.Instance);
        Assert.True(watcher.TryStart(folder.Path, path => pipeline.Submit(path, CancellationToken.None)));

        folder.WriteXml("nuevo.xml");
        await TestWait.UntilAsync(() => processor.CompletedCount == 1, TimeSpan.FromSeconds(5));

        watcher.Stop();
        await pipeline.ShutdownAsync();
    }

    [Theory]
    [InlineData("ticket.xml")]
    [InlineData("ticket.XML")]
    [InlineData("ticket.Xml")]
    public void Scanner_acepta_extension_xml_sin_importar_mayusculas(string fileName)
    {
        using var folder = new TempFolder();
        folder.WriteXml(fileName);
        var discovered = new List<string>();
        var scanner = new InputDirectoryScanner(TimeProvider.System, NullLogger<InputDirectoryScanner>.Instance);

        scanner.ScanOnce(folder.Path, discovered.Add);

        Assert.Single(discovered);
        Assert.True(FilePathNormalizer.HasXmlExtension(discovered[0]));
    }

    [Fact]
    public void Scanner_no_descubre_ficheros_que_no_son_xml()
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder.Xml("nota.txt"), "no");
        File.WriteAllText(folder.Xml("ticket.xml.bak"), "no");
        var discovered = new List<string>();
        var scanner = new InputDirectoryScanner(TimeProvider.System, NullLogger<InputDirectoryScanner>.Instance);

        scanner.ScanOnce(folder.Path, discovered.Add);

        Assert.Empty(discovered);
    }
}
