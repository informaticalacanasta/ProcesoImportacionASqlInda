using System.Collections.Concurrent;
using DbInda.Worker.Configuration;
using DbInda.Worker.Inbound;
using Microsoft.Extensions.Options;

namespace DbInda.Worker.Processing;

public sealed class SqlRetryScheduler
{
    private readonly ConcurrentDictionary<string, RetryState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly RetryOptions _options;
    private readonly TimeProvider _timeProvider;

    public SqlRetryScheduler(IOptions<RetryOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public bool ShouldDefer(string path, out DateTimeOffset nextAttemptUtc)
    {
        var key = FilePathNormalizer.Normalize(path);
        if (_states.TryGetValue(key, out var state) && state.NextAttemptUtc > _timeProvider.GetUtcNow())
        {
            nextAttemptUtc = state.NextAttemptUtc;
            return true;
        }

        nextAttemptUtc = default;
        return false;
    }

    public TimeSpan RegisterFailure(string path)
    {
        var key = FilePathNormalizer.Normalize(path);
        var now = _timeProvider.GetUtcNow();
        TimeSpan delay = default;
        _states.AddOrUpdate(
            key,
            _ =>
            {
                delay = ComputeDelay(1);
                return new RetryState(1, now + delay);
            },
            (_, previous) =>
            {
                var failures = previous.Failures + 1;
                delay = ComputeDelay(failures);
                return new RetryState(failures, now + delay);
            });
        return delay;
    }

    public void Clear(string path)
    {
        _states.TryRemove(FilePathNormalizer.Normalize(path), out _);
    }

    private TimeSpan ComputeDelay(int failures)
    {
        var seconds = _options.InitialDelaySeconds * Math.Pow(_options.BackoffMultiplier, Math.Max(0, failures - 1));
        seconds = Math.Min(seconds, _options.MaxDelaySeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private readonly record struct RetryState(int Failures, DateTimeOffset NextAttemptUtc);
}
