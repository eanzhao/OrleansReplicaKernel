using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Demo;

public sealed partial class EchoGrain : IEchoGrain, IActivationHandoffParticipant
{
    private static string? _nextCaptureFailureReason;
    private static string? _nextApplyFailureReason;
    private readonly List<string> _timerEvents = [];
    private int _callCount;

    public static void FailNextWarmHandoffCapture(string reason)
    {
        Interlocked.Exchange(ref _nextCaptureFailureReason, reason);
    }

    public static void FailNextWarmHandoffApply(string reason)
    {
        Interlocked.Exchange(ref _nextApplyFailureReason, reason);
    }

    public Task<string> PingAsync(string text, CancellationToken cancellationToken = default)
    {
        _callCount++;
        TraceLog.Write("grain", $"EchoGrain handle PingAsync(\"{text}\") count={_callCount}");
        return Task.FromResult($"echo:{text}:count={_callCount}");
    }

    public async Task<string> PingSlowAsync(string text, int delayMs, CancellationToken cancellationToken = default)
    {
        TraceLog.Write("grain", $"EchoGrain begin PingSlowAsync(\"{text}\") delay={delayMs}ms");
        await DelayAsync(delayMs, cancellationToken);
        _callCount++;
        TraceLog.Write("grain", $"EchoGrain end PingSlowAsync(\"{text}\") count={_callCount}");
        return $"echo:{text}:count={_callCount}";
    }

    public async Task<string> PingWithObserverAsync(
        string text,
        IEchoObserver observer,
        CancellationToken cancellationToken = default)
    {
        _callCount++;
        var value = $"observer:{text}:count={_callCount}";
        TraceLog.Write("grain", $"EchoGrain handle PingWithObserverAsync(\"{text}\") count={_callCount}");

        await observer.OnEchoAsync(value, cancellationToken);

        return $"echo:{text}:count={_callCount}";
    }

    public async Task<int> ReentrantSelfCallAsync(int remaining, CancellationToken cancellationToken = default)
    {
        _callCount++;
        TraceLog.Write("grain", $"EchoGrain handle ReentrantSelfCallAsync(remaining={remaining}) count={_callCount}");

        if (remaining <= 0)
        {
            return _callCount;
        }

        var runtime = ActivationExecutionContext.CurrentRuntime
            ?? throw new InvalidOperationException("No activation runtime is available for reentrant self-call.");
        var grainId = ActivationExecutionContext.CurrentGrainId
            ?? throw new InvalidOperationException("No current grain identity is available for reentrant self-call.");
        var self = new EchoGrainReference(runtime, grainId);
        return await self.ReentrantSelfCallAsync(remaining - 1, cancellationToken);
    }

    public async Task ArmOneShotTimerAsync(string timerName, int delayMs, CancellationToken cancellationToken = default)
    {
        var timerRegistry = ActivationExecutionContext.CurrentTimerRegistry
            ?? throw new InvalidOperationException("No activation timer registry is available for timer registration.");

        await timerRegistry.RegisterTimerAsync(
            timerName,
            TimeSpan.FromMilliseconds(delayMs),
            period: null,
            _ => OnTimerAsync(timerName));
        TraceLog.Write("grain", $"EchoGrain arm one-shot timer \"{timerName}\" delay={delayMs}ms");
    }

    public async Task<string> HoldTurnWithTimerAsync(
        string timerName,
        int holdDelayMs,
        int timerDelayMs,
        CancellationToken cancellationToken = default)
    {
        _callCount++;
        TraceLog.Write(
            "grain",
            $"EchoGrain begin HoldTurnWithTimerAsync(\"{timerName}\") hold={holdDelayMs}ms timer={timerDelayMs}ms count={_callCount}");

        var timerRegistry = ActivationExecutionContext.CurrentTimerRegistry
            ?? throw new InvalidOperationException("No activation timer registry is available for timer registration.");

        await timerRegistry.RegisterTimerAsync(
            timerName,
            TimeSpan.FromMilliseconds(timerDelayMs),
            period: null,
            _ => OnTimerAsync(timerName));

        await DelayAsync(holdDelayMs, cancellationToken);
        TraceLog.Write("grain", $"EchoGrain end HoldTurnWithTimerAsync(\"{timerName}\") count={_callCount}");
        return $"hold:{timerName}:count={_callCount}";
    }

    public Task<string> GetTimerSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _timerEvents.Count == 0
            ? "<none>"
            : string.Join(", ", _timerEvents);
        TraceLog.Write("grain", $"EchoGrain timer snapshot {snapshot}");
        return Task.FromResult(snapshot);
    }

    public object CaptureHandoffState()
    {
        var failureReason = Interlocked.Exchange(ref _nextCaptureFailureReason, null);
        if (failureReason is not null)
        {
            throw new InvalidOperationException(failureReason);
        }

        TraceLog.Write("grain", $"EchoGrain capture warm handoff state count={_callCount}");
        return new EchoHandoffState(_callCount);
    }

    public void ApplyHandoffState(object? state)
    {
        var failureReason = Interlocked.Exchange(ref _nextApplyFailureReason, null);
        if (failureReason is not null)
        {
            throw new InvalidOperationException(failureReason);
        }

        if (state is not EchoHandoffState handoffState)
        {
            throw new InvalidOperationException("EchoGrain received an invalid warm handoff state payload.");
        }

        _callCount = handoffState.CallCount;
        TraceLog.Write("grain", $"EchoGrain apply warm handoff state count={_callCount}");
    }

    private sealed record EchoHandoffState(int CallCount);

    private static Task DelayAsync(int delayMs, CancellationToken cancellationToken)
    {
        var timeProvider = ActivationExecutionContext.CurrentTimeProvider;
        return timeProvider is null
            ? Task.Delay(delayMs, cancellationToken)
            : Task.Delay(TimeSpan.FromMilliseconds(delayMs), timeProvider, cancellationToken);
    }

    private ValueTask OnTimerAsync(string timerName)
    {
        _callCount++;
        var value = $"timer:{timerName}:count={_callCount}";
        _timerEvents.Add(value);
        TraceLog.Write("timer", $"EchoGrain timer \"{timerName}\" fire count={_callCount}");
        return ValueTask.CompletedTask;
    }
}
