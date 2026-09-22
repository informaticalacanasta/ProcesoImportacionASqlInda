using System.Diagnostics;
using DbInda.Tests.Inbound;
using DbInda.Worker.Configuration;
using DbInda.Worker.Files;
using DbInda.Worker.Inbound;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace DbInda.Tests.Files;

// Local Windows timings. They do not predict Ubuntu or real SQL.
public sealed class OrganizationLoadTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Mide_rafaga_llegadas_sostenidas_e_incidencias()
    {
        using var root = new TempFolder();
        var production = new ProcessingOptions
        {
            StableChecks = 3,
            StableCheckDelayMilliseconds = 1000,
            MaxConcurrency = 20,
            QueueCapacity = 100,
            ScanIntervalSeconds = 10
        };

        var single = root.WriteXml("baseline.txt", "base");
        var checker = new FileReadinessChecker(Options.Create(production), TimeProvider.System, new FileSystemStabilityProbe(),
            NullLogger<FileReadinessChecker>.Instance);
        var baselineClock = Stopwatch.StartNew();
        Assert.True(await checker.WaitUntilReadyAsync(single, default));
        Assert.True(await checker.WaitUntilReadyAsync(single, default));
        var baselineMs = baselineClock.ElapsedMilliseconds;
        File.Delete(single);

        var organizer = Organizer(root, production, maxConcurrency: 4, readinessTimeoutSeconds: 15);
        for (var i = 0; i < 4; i++)
        {
            root.WriteXml($"{81000 + i}_P{i}.TXT", "rafaga-" + i);
            root.WriteXml($"bnd{81000 + i}", "");
        }
        var burstClock = Stopwatch.StartNew();
        await organizer.ScanAsync(default);
        var burstMs = burstClock.ElapsedMilliseconds;
        for (var i = 0; i < 4; i++)
            Assert.Equal("rafaga-" + i, File.ReadAllText(root.Xml($"inboxOrganizado/P{i}.TXT")));

        var arrived = 0;
        var maxPending = 0;
        var sustainedClock = Stopwatch.StartNew();
        var producer = Task.Run(async () =>
        {
            for (var i = 0; i < 4; i++)
            {
                root.WriteXml($"{82000 + i}_S{i}.TXT", "sostenido-" + i);
                root.WriteXml($"bnd{82000 + i}", "");
                Interlocked.Add(ref arrived, 2);
                await Task.Delay(1000);
            }
        });
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            maxPending = Math.Max(maxPending, Directory.GetFiles(root.Path).Length);
            await organizer.ScanAsync(default);
            if (producer.IsCompleted && Directory.GetFiles(root.Path).Length == 0)
                break;
        }
        await producer;
        var sustainedMs = sustainedClock.ElapsedMilliseconds;
        Assert.Empty(Directory.GetFiles(root.Path));
        for (var i = 0; i < 4; i++)
            Assert.Equal("sostenido-" + i, File.ReadAllText(root.Xml($"inboxOrganizado/S{i}.TXT")));
        Assert.True(maxPending < 16, $"Pendientes máximos {maxPending}");
        output.WriteLine($"Doble espera completa de un archivo: {baselineMs} ms");
        output.WriteLine($"Ráfaga de 4 TXT + 4 bnd, concurrencia 4: {burstMs} ms");
        output.WriteLine($"Sostenido 1 par/s durante 4 s: {sustainedMs} ms, llegadas {arrived}, pendientes máximos {maxPending}");

        var blocked = root.WriteXml("83001_LENTO.TXT", "lento");
        root.WriteXml("bnd83001", "");
        root.WriteXml("83002_RAPIDO.TXT", "rapido");
        root.WriteXml("bnd83002", "");
        root.WriteXml("factura_sin_firmar.pdf", "pdf");
        root.WriteXml("factura_a4_sin_firmar.pdf", "a4");
        using (new FileStream(blocked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var incident = Organizer(root, production, 4, 8);
            var incidentClock = Stopwatch.StartNew();
            await incident.ScanAsync(default);
            output.WriteLine($"Incidencias con archivo bloqueado: {incidentClock.ElapsedMilliseconds} ms");
        }
        Assert.True(File.Exists(blocked));
        Assert.Equal("rapido", File.ReadAllText(root.Xml("inboxOrganizado/RAPIDO.TXT")));
        Assert.True(File.Exists(root.Xml("factura_sin_firmar.pdf")));
        Assert.True(File.Exists(root.Xml("factura_a4_sin_firmar.pdf")));
        root.WriteXml("83003_RAPIDO.TXT", "otro");
        root.WriteXml("bnd83003", "");
        await Organizer(root, PipelineFactory.FastOptions(stableChecks: 1), 4, 10).ScanAsync(default);
        Assert.Equal("rapido", File.ReadAllText(root.Xml("inboxOrganizado/RAPIDO.TXT")));
        Assert.Contains("otro", Directory.GetFiles(root.Xml("inbox"), "*RAPIDO*").Select(File.ReadAllText));

        output.WriteLine("Entorno: Windows, disco local, estabilidad 3 x 1000 ms. No incluye SQL real ni Ubuntu.");
    }

    private static ReceivedFileOrganizer Organizer(TempFolder root, ProcessingOptions processing, int maxConcurrency,
        int readinessTimeoutSeconds, IArchivedInvoiceLookup? lookup = null)
    {
        var readiness = new FileReadinessChecker(Options.Create(processing), TimeProvider.System, new FileSystemStabilityProbe(),
            NullLogger<FileReadinessChecker>.Instance);
        return new ReceivedFileOrganizer(
            Options.Create(new PathsOptions { Input = root.Path }),
            Options.Create(new OrganizationOptions { MaxConcurrency = maxConcurrency, ReadinessTimeoutSeconds = readinessTimeoutSeconds }),
            readiness, lookup ?? new CountingLookup(), NullLogger<ReceivedFileOrganizer>.Instance);
    }

    private sealed class CountingLookup : IArchivedInvoiceLookup
    {
        public Task<IReadOnlyList<string>> FindAsync(string originXmlPath, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
