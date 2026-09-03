using DbInda.Worker.Configuration;
using DbInda.Worker.Processing;
using Microsoft.Extensions.Options;

namespace DbInda.Tests.Inbound;

public sealed class SqlRetrySchedulerTests
{
    [Fact]
    public async Task Backoff_no_encola_el_mismo_fichero_ni_bloquea_a_los_demas()
    {
        var clock = new ControllableTimeProvider { Now = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero) };
        var retries = new SqlRetryScheduler(
            Options.Create(new RetryOptions
            {
                InitialDelaySeconds = 60,
                MaxDelaySeconds = 300,
                BackoffMultiplier = 2
            }),
            clock);
        retries.RegisterFailure(@"C:\DbInda\Entrada\fallo.xml");

        var (pipeline, processor) = PipelineFactory.Create(retries: retries);
        pipeline.Start();
        pipeline.Submit(@"C:\DbInda\Entrada\fallo.xml", CancellationToken.None);
        pipeline.Submit(@"C:\DbInda\Entrada\otro.xml", CancellationToken.None);

        await TestWait.UntilAsync(() => processor.CompletedCount == 1);
        Assert.Contains(processor.CompletedPaths, p => p.Contains("otro.xml", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(processor.CompletedPaths, p => p.Contains("fallo.xml", StringComparison.OrdinalIgnoreCase));

        clock.Now = clock.Now.AddMinutes(2);
        pipeline.Submit(@"C:\DbInda\Entrada\fallo.xml", CancellationToken.None);
        await TestWait.UntilAsync(() => processor.CompletedCount == 2);
        await pipeline.ShutdownAsync();
    }

    [Fact]
    public void El_delay_crece_hasta_el_maximo()
    {
        var clock = new ControllableTimeProvider { Now = DateTimeOffset.UtcNow };
        var retries = new SqlRetryScheduler(
            Options.Create(new RetryOptions
            {
                InitialDelaySeconds = 5,
                MaxDelaySeconds = 20,
                BackoffMultiplier = 2
            }),
            clock);

        var first = retries.RegisterFailure(@"C:\a.xml");
        Assert.Equal(TimeSpan.FromSeconds(5), first);
        clock.Now += TimeSpan.FromHours(1);
        var second = retries.RegisterFailure(@"C:\a.xml");
        Assert.Equal(TimeSpan.FromSeconds(10), second);
        clock.Now += TimeSpan.FromHours(1);
        var third = retries.RegisterFailure(@"C:\a.xml");
        Assert.Equal(TimeSpan.FromSeconds(20), third);
    }

    private sealed class ControllableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
