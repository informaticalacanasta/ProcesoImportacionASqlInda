namespace DbInda.Tests.Inbound;

internal sealed class ImmediateTimeProvider : TimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ImmediateTimer(callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class ImmediateTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private int _disposed;

        public ImmediateTimer(TimerCallback callback, object? state)
        {
            _callback = callback;
            _state = state;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (Volatile.Read(ref _disposed) == 1)
                return false;
            if (dueTime == Timeout.InfiniteTimeSpan)
                return true;

            ThreadPool.UnsafeQueueUserWorkItem(_ => _callback(_state), null);
            return true;
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
