using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Routing;
using OrleansReplicaKernel.Scheduling;

namespace OrleansReplicaKernel.Runtime;

public sealed class ActivationEntry : IAsyncDisposable
{
    private const int ActiveState = 0;
    private const int QuiescingState = 1;
    private const int DisposedState = 2;

    private readonly object _instance;
    private readonly object _quiesceLock = new();
    private readonly ActivationScheduler _scheduler;
    private long _lastTouchedUtcTicks;
    private int _lifecycleState;
    private int _pendingInvocationCount;
    private TaskCompletionSource<bool>? _quiescedCompletion;

    public ActivationEntry(GrainId grainId, object instance, long ownerVersion)
        : this(grainId, instance, DateTimeOffset.UtcNow, ownerVersion, isRecovered: false)
    {
    }

    private ActivationEntry(
        GrainId grainId,
        object instance,
        DateTimeOffset lastTouchedUtc,
        long ownerVersion,
        bool isRecovered)
    {
        GrainId = grainId;
        OwnerVersion = ownerVersion;
        _instance = instance;
        _scheduler = new ActivationScheduler(grainId.ToString());
        _lastTouchedUtcTicks = lastTouchedUtc.UtcTicks;

        TraceLog.Write(
            "activation",
            isRecovered
                ? $"recover activation metadata {GrainId} -> {instance.GetType().Name} last-touched={lastTouchedUtc:O}"
                : $"create {GrainId} -> {instance.GetType().Name}");
    }

    public GrainId GrainId { get; }

    public long OwnerVersion { get; }

    public string InstanceTypeName => _instance.GetType().Name;

    public DateTimeOffset LastTouchedUtc => new(Interlocked.Read(ref _lastTouchedUtcTicks), TimeSpan.Zero);

    public async ValueTask<object?> InvokeAsync(
        InvocationMessage message,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (Volatile.Read(ref _lifecycleState) != ActiveState)
            {
                throw new ActivationQuiescingException(GrainId);
            }

            Interlocked.Increment(ref _pendingInvocationCount);
            if (Volatile.Read(ref _lifecycleState) == ActiveState)
            {
                break;
            }

            if (Interlocked.Decrement(ref _pendingInvocationCount) == 0)
            {
                SignalQuiescedIfNeeded();
            }

            throw new ActivationQuiescingException(GrainId);
        }

        Touch();

        try
        {
            return await _scheduler.EnqueueAsync(
                $"{message.Invokable.InterfaceName}.{message.Invokable.MethodName}",
                async turnToken =>
                {
                    TraceLog.Write("activation", $"dispatch {message.Invokable.MethodName} to {GrainId}");
                    return await message.Invokable.InvokeAsync(_instance, turnToken);
                },
                cancellationToken);
        }
        finally
        {
            Touch();
            if (Interlocked.Decrement(ref _pendingInvocationCount) == 0)
            {
                SignalQuiescedIfNeeded();
            }
        }
    }

    public async ValueTask QuiesceAsync()
    {
        var previousState = Interlocked.CompareExchange(ref _lifecycleState, QuiescingState, ActiveState);
        if (previousState == DisposedState)
        {
            return;
        }

        TraceLog.Write("activation", $"begin quiesce {GrainId}");
        await WaitForDrainAsync();
        TraceLog.Write("activation", $"drained quiescing activation {GrainId}");
    }

    public bool CanCollect(DateTimeOffset utcNow, TimeSpan idleFor)
    {
        if (Volatile.Read(ref _pendingInvocationCount) != 0)
        {
            return false;
        }

        return utcNow - LastTouchedUtc >= idleFor;
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _lifecycleState, DisposedState);
        TraceLog.Write("activation", $"deactivate {GrainId}");
        await _scheduler.DisposeAsync();

        switch (_instance)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    public ActivationMetadataRecord ExportMetadata()
        => new(
            GrainId,
            InstanceTypeName,
            LastTouchedUtc,
            OwnerVersion);

    public ActivationHandoffRecord? CaptureHandoffState()
    {
        if (_instance is not IActivationHandoffParticipant participant)
        {
            return null;
        }

        var capturedUtc = DateTimeOffset.UtcNow;
        var payload = participant.CaptureHandoffState();
        Touch();
        TraceLog.Write("activation", $"capture warm handoff state {GrainId} at {capturedUtc:O}");
        return new ActivationHandoffRecord(GrainId, InstanceTypeName, payload, capturedUtc);
    }

    public void ApplyHandoffState(ActivationHandoffRecord handoffState)
    {
        if (_instance is not IActivationHandoffParticipant participant)
        {
            throw new InvalidOperationException(
                $"Activation '{GrainId}' does not implement {nameof(IActivationHandoffParticipant)}.");
        }

        participant.ApplyHandoffState(handoffState.Payload);
        Touch();
        TraceLog.Write(
            "activation",
            $"apply warm handoff state {GrainId} captured={handoffState.CapturedUtc:O}");
    }

    public static ActivationEntry Restore(ActivationMetadataRecord metadata, object instance)
        => new(metadata.GrainId, instance, metadata.LastTouchedUtc, metadata.OwnerVersion, isRecovered: true);

    private Task WaitForDrainAsync()
    {
        if (Volatile.Read(ref _pendingInvocationCount) == 0)
        {
            return Task.CompletedTask;
        }

        lock (_quiesceLock)
        {
            if (Volatile.Read(ref _pendingInvocationCount) == 0)
            {
                return Task.CompletedTask;
            }

            _quiescedCompletion ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _quiescedCompletion.Task;
        }
    }

    private void SignalQuiescedIfNeeded()
    {
        if (Volatile.Read(ref _lifecycleState) != QuiescingState
            || Volatile.Read(ref _pendingInvocationCount) != 0)
        {
            return;
        }

        lock (_quiesceLock)
        {
            if (Volatile.Read(ref _lifecycleState) == QuiescingState
                && Volatile.Read(ref _pendingInvocationCount) == 0)
            {
                _quiescedCompletion?.TrySetResult(true);
            }
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastTouchedUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
}
