using System.Collections.Concurrent;
using DbInda.Worker.Configuration;
using DbInda.Worker.Inbound;
using DbInda.Worker.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DbInda.Tests.Inbound;

internal static class TestWait
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > limit)
                throw new TimeoutException("La condición de test no se cumplió a tiempo.");
            await Task.Delay(10);
        }
    }
}

internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateDirectory(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dbinda-3a-" + Guid.NewGuid().ToString("N"))).FullName;

    public string Xml(string name) => System.IO.Path.Combine(Path, name);

    public string WriteXml(string name, string contents = "<TicketBai />")
    {
        var path = Xml(name);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
        }
    }
}

internal sealed class AlwaysReadyProbe : IFileStabilityProbe
{
    public bool Exists(string path) => true;

    public bool TryObserve(string path, out long length, out DateTime lastWriteUtc)
    {
        length = 1;
        lastWriteUtc = new DateTime(2026, 8, 15, 10, 7, 59, DateTimeKind.Utc);
        return true;
    }
}

internal sealed class ScriptedFileProbe : IFileStabilityProbe
{
    private int _observeIndex;

    public List<(bool Ok, long Length, DateTime Utc)> Script { get; init; } = [];

    public int ObserveCount => _observeIndex;

    public bool Exists(string path) => _observeIndex < Script.Count;

    public bool TryObserve(string path, out long length, out DateTime lastWriteUtc)
    {
        if (_observeIndex >= Script.Count)
        {
            length = 0;
            lastWriteUtc = default;
            return false;
        }

        var item = Script[_observeIndex++];
        length = item.Length;
        lastWriteUtc = item.Utc;
        return item.Ok;
    }
}

internal sealed class RecordingProcessor : IInboundFileProcessor
{
    private readonly Func<string, CancellationToken, Task>? _inner;

    public RecordingProcessor(Func<string, CancellationToken, Task>? inner = null)
        => _inner = inner;

    public ConcurrentQueue<string> StartedPaths { get; } = new();
    public ConcurrentQueue<string> CompletedPaths { get; } = new();

    public int StartedCount => StartedPaths.Count;
    public int CompletedCount => CompletedPaths.Count;

    public async Task ProcessAsync(string fullPath, CancellationToken cancellationToken)
    {
        StartedPaths.Enqueue(fullPath);
        if (_inner is not null)
            await _inner(fullPath, cancellationToken).ConfigureAwait(false);
        CompletedPaths.Enqueue(fullPath);
    }
}

internal static class PipelineFactory
{
    public static ProcessingOptions FastOptions(
        int maxConcurrency = 2,
        int queueCapacity = 10,
        int stableChecks = 1)
        => new()
        {
            MaxConcurrency = maxConcurrency,
            QueueCapacity = queueCapacity,
            ScanIntervalSeconds = 60,
            StableChecks = stableChecks,
            StableCheckDelayMilliseconds = 1
        };

    public static (InboundXmlPipeline Pipeline, RecordingProcessor Processor) Create(
        ProcessingOptions? options = null,
        IFileStabilityProbe? probe = null,
        TimeProvider? timeProvider = null,
        RecordingProcessor? processor = null,
        SqlRetryScheduler? retries = null)
    {
        options ??= FastOptions();
        processor ??= new RecordingProcessor();
        var time = timeProvider ?? new ImmediateTimeProvider();
        var readiness = new FileReadinessChecker(
            Options.Create(options),
            time,
            probe ?? new AlwaysReadyProbe(),
            NullLogger<FileReadinessChecker>.Instance);
        var pipeline = new InboundXmlPipeline(
            Options.Create(options),
            readiness,
            processor,
            retries ?? new SqlRetryScheduler(Options.Create(new RetryOptions()), time),
            NullLogger<InboundXmlPipeline>.Instance);
        return (pipeline, processor);
    }
}
