using System.Threading;

namespace OrleansReplicaKernel.Tests.TestSupport;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private const long TimestampTicksPerSecond = TimeSpan.TicksPerSecond;

    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTimeOffset UtcNow { get; private set; }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return UtcNow;
        }
    }

    public override long TimestampFrequency => TimestampTicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_lock)
        {
            return UtcNow.UtcTicks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);

        lock (_lock)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "Manual time can only advance forward.");
        }

        List<(TimerCallback Callback, object? State)> dueCallbacks = [];

        lock (_lock)
        {
            UtcNow = UtcNow.Add(delta);
            foreach (var timer in _timers.ToArray())
            {
                timer.CollectDueCallbacks(UtcNow, dueCallbacks);
            }
        }

        foreach (var (callback, state) in dueCallbacks)
        {
            callback(state);
        }
    }

    private static void ValidateTimerWindow(TimeSpan value, string paramName)
    {
        if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(paramName);
        }
    }

    private sealed class ManualTimer : ITimer, IDisposable, IAsyncDisposable
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private bool _disposed;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private DateTimeOffset? _nextTickUtc;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ValidateTimerWindow(dueTime, nameof(dueTime));
            ValidateTimerWindow(period, nameof(period));

            lock (_owner._lock)
            {
                if (_disposed)
                {
                    return false;
                }

                _period = period;
                _nextTickUtc = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : _owner.UtcNow.Add(dueTime);
                return true;
            }
        }

        public void CollectDueCallbacks(
            DateTimeOffset utcNow,
            List<(TimerCallback Callback, object? State)> dueCallbacks)
        {
            if (_disposed || _nextTickUtc is null || _nextTickUtc > utcNow)
            {
                return;
            }

            if (_period == Timeout.InfiniteTimeSpan)
            {
                dueCallbacks.Add((_callback, _state));
                _nextTickUtc = null;
                return;
            }

            while (!_disposed && _nextTickUtc is not null && _nextTickUtc <= utcNow)
            {
                dueCallbacks.Add((_callback, _state));
                _nextTickUtc = _nextTickUtc.Value.Add(_period);
            }
        }

        public void Dispose()
        {
            lock (_owner._lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _nextTickUtc = null;
                _owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
