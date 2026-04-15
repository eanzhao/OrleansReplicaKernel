namespace OrleansReplicaKernel.Runtime;

internal sealed class ActivationTimerRegistration : IActivationTimerHandle
{
    private readonly Guid _timerId;
    private readonly TimeProvider _timeProvider;
    private readonly Func<CancellationToken, ValueTask> _onTick;
    private readonly Action<Guid> _onCompleted;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _disposeRequested;

    public ActivationTimerRegistration(
        Guid timerId,
        string timerName,
        TimeSpan dueTime,
        TimeSpan? period,
        TimeProvider timeProvider,
        Func<CancellationToken, ValueTask> onTick,
        Action<Guid> onCompleted)
    {
        _timerId = timerId;
        TimerName = timerName;
        DueTime = dueTime;
        Period = period;
        _timeProvider = timeProvider;
        _onTick = onTick;
        _onCompleted = onCompleted;
        _loop = Task.Run(RunAsync);
    }

    public string TimerName { get; }

    public TimeSpan DueTime { get; }

    public TimeSpan? Period { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) == 0)
        {
            _cts.Cancel();
        }

        try
        {
            await _loop;
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await Task.Delay(DueTime, _timeProvider, _cts.Token);

            while (!_cts.IsCancellationRequested)
            {
                await _onTick(_cts.Token);

                if (Period is null)
                {
                    break;
                }

                await Task.Delay(Period.Value, _timeProvider, _cts.Token);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        finally
        {
            _onCompleted(_timerId);
        }
    }
}
