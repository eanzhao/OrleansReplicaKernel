using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Demo;

public sealed partial class EchoGrain : IEchoGrain, IActivationHandoffParticipant
{
    private static string? _nextCaptureFailureReason;
    private static string? _nextApplyFailureReason;
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
        await Task.Delay(delayMs, cancellationToken);
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
}
