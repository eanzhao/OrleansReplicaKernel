using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Routing;
using OrleansReplicaKernel.Reminders;
using OrleansReplicaKernel.Scheduling;

namespace OrleansReplicaKernel.Runtime;

public sealed class ActivationEntry : IAsyncDisposable, IActivationTimerRegistry
{
    private const int ActiveState = 0;
    private const int QuiescingState = 1;
    private const int DisposedState = 2;
    private const int ActivationPendingState = 0;
    private const int ActivationRunningState = 1;
    private const int ActivationCompletedState = 2;
    private const int ActivationFailedState = 3;

    private readonly object _instance;
    private readonly object _activationLock = new();
    private readonly object _quiesceLock = new();
    private readonly object _timerLock = new();
    private readonly TimeProvider _timeProvider;
    private readonly ActivationScheduler _scheduler;
    private readonly GrainTypeSchedulingPolicy _schedulingPolicy;
    private readonly Dictionary<Guid, ActivationTimerRegistration> _timers = new();
    private long _lastTouchedUtcTicks;
    private int _lifecycleState;
    private int _activationState;
    private int _pendingInvocationCount;
    private TaskCompletionSource<bool>? _activationCompletion;
    private IInvocationRuntime? _activationRuntime;
    private TaskCompletionSource<bool>? _quiescedCompletion;

    public ActivationEntry(
        GrainId grainId,
        object instance,
        long ownerVersion,
        GrainTypeSchedulingPolicy schedulingPolicy,
        TimeProvider timeProvider)
        : this(
            grainId,
            instance,
            timeProvider.GetUtcNow(),
            ownerVersion,
            schedulingPolicy,
            timeProvider,
            isRecovered: false)
    {
    }

    private ActivationEntry(
        GrainId grainId,
        object instance,
        DateTimeOffset lastTouchedUtc,
        long ownerVersion,
        GrainTypeSchedulingPolicy schedulingPolicy,
        TimeProvider timeProvider,
        bool isRecovered)
    {
        GrainId = grainId;
        OwnerVersion = ownerVersion;
        _instance = instance;
        _timeProvider = timeProvider;
        _schedulingPolicy = schedulingPolicy;
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
        IInvocationRuntime runtime,
        CancellationToken cancellationToken)
    {
        EnterActiveTurn();
        Touch();

        try
        {
            _activationRuntime = runtime;
            await EnsureActivatedAsync(runtime, cancellationToken);
            var allowInterleaving = _schedulingPolicy.AllowsInterleaving(message.Invokable.MethodName);
            return await _scheduler.EnqueueAsync(
                $"{message.Invokable.InterfaceName}.{message.Invokable.MethodName}",
                allowInterleaving,
                message.RequestChainId,
                turnToken => ActivationExecutionContext.RunAsync(
                    runtime,
                    GrainId,
                    message.RequestChainId,
                    this,
                    ResolveReminderRegistry(runtime),
                    _timeProvider,
                    async () =>
                    {
                        TraceLog.Write("activation", $"dispatch {message.Invokable.MethodName} to {GrainId}");
                        return await message.Invokable.InvokeAsync(_instance, turnToken);
                    }),
                cancellationToken);
        }
        finally
        {
            ExitActiveTurn();
        }
    }

    public ValueTask<IActivationTimerHandle> RegisterTimerAsync(
        string timerName,
        TimeSpan dueTime,
        TimeSpan? period,
        Func<CancellationToken, ValueTask> callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timerName);
        ArgumentNullException.ThrowIfNull(callback);

        if (dueTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(dueTime), "Timer due time must be non-negative.");
        }

        if (period is not null && period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period), "Timer period must be positive when provided.");
        }

        EnsureActiveForTimerRegistration();

        var runtime = ActivationExecutionContext.CurrentRuntime
            ?? throw new InvalidOperationException("Activation timer registration requires an active invocation runtime.");

        var timerId = Guid.NewGuid();
        var timer = new ActivationTimerRegistration(
            timerId,
            timerName,
            dueTime,
            period,
            _timeProvider,
            cancellationToken => FireTimerAsync(timerName, runtime, callback, cancellationToken),
            RemoveTimer);

        lock (_timerLock)
        {
            _timers.Add(timerId, timer);
        }

        TraceLog.Write(
            "timer",
            $"register {timerName} on {GrainId} due={dueTime}{(period is null ? string.Empty : $" period={period}")}");
        return ValueTask.FromResult<IActivationTimerHandle>(timer);
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

    public ValueTask DisposeAsync() => DisposeAsync(ActivationDeactivationReason.Shutdown);

    public async ValueTask DisposeAsync(ActivationDeactivationReason reason)
    {
        Interlocked.Exchange(ref _lifecycleState, DisposedState);
        TraceLog.Write("activation", $"deactivate {GrainId} reason={reason}");

        List<ActivationTimerRegistration> timers;
        lock (_timerLock)
        {
            timers = _timers.Values.ToList();
            _timers.Clear();
        }

        foreach (var timer in timers)
        {
            await timer.DisposeAsync();
        }

        await InvokeDeactivateAsync(reason);
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

        var capturedUtc = _timeProvider.GetUtcNow();
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

    public static ActivationEntry Restore(
        ActivationMetadataRecord metadata,
        object instance,
        GrainTypeSchedulingPolicy schedulingPolicy,
        TimeProvider timeProvider)
        => new(
            metadata.GrainId,
            instance,
            metadata.LastTouchedUtc,
            metadata.OwnerVersion,
            schedulingPolicy,
            timeProvider,
            isRecovered: true);

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

    private async ValueTask EnsureActivatedAsync(
        IInvocationRuntime runtime,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var state = Volatile.Read(ref _activationState);
            if (state == ActivationCompletedState)
            {
                return;
            }

            if (state == ActivationFailedState)
            {
                throw CreateActivationFailureException();
            }

            Task completionTask;
            var shouldRun = false;

            lock (_activationLock)
            {
                switch (_activationState)
                {
                    case ActivationCompletedState:
                        return;
                    case ActivationFailedState:
                        throw CreateActivationFailureException();
                    case ActivationPendingState:
                        _activationRuntime = runtime;
                        _activationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _activationState = ActivationRunningState;
                        completionTask = _activationCompletion.Task;
                        shouldRun = true;
                        break;
                    default:
                        completionTask = _activationCompletion?.Task ?? Task.CompletedTask;
                        break;
                }
            }

            if (shouldRun)
            {
                await RunActivationAsync(runtime);
                return;
            }

            await completionTask.WaitAsync(cancellationToken);
        }
    }

    private void EnterActiveTurn()
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
                return;
            }

            if (Interlocked.Decrement(ref _pendingInvocationCount) == 0)
            {
                SignalQuiescedIfNeeded();
            }

            throw new ActivationQuiescingException(GrainId);
        }
    }

    private void ExitActiveTurn()
    {
        Touch();
        if (Interlocked.Decrement(ref _pendingInvocationCount) == 0)
        {
            SignalQuiescedIfNeeded();
        }
    }

    private async ValueTask FireTimerAsync(
        string timerName,
        IInvocationRuntime runtime,
        Func<CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken)
    {
        try
        {
            EnterActiveTurn();
        }
        catch (ActivationQuiescingException)
        {
            TraceLog.Write("timer", $"skip {timerName} on {GrainId}: activation is quiescing");
            return;
        }

        var requestChainId = Guid.NewGuid();
        Touch();

        try
        {
            await _scheduler.EnqueueAsync(
                $"$timer.{timerName}",
                allowInterleaving: false,
                requestChainId,
                async _ =>
                {
                    await ActivationExecutionContext.RunAsync(
                        runtime,
                        GrainId,
                        requestChainId,
                        this,
                        ResolveReminderRegistry(runtime),
                        _timeProvider,
                        async () =>
                        {
                            TraceLog.Write("timer", $"fire {timerName} on {GrainId} chain={requestChainId:N}");
                            await callback(cancellationToken);
                            return (object?)null;
                        });
                    return null;
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            ExitActiveTurn();
        }
    }

    private async ValueTask RunActivationAsync(IInvocationRuntime runtime)
    {
        var requestChainId = Guid.NewGuid();

        try
        {
            await _scheduler.EnqueueAsync(
                "$activate",
                allowInterleaving: false,
                requestChainId,
                async turnToken =>
                {
                    await ActivationExecutionContext.RunAsync(
                        runtime,
                        GrainId,
                        requestChainId,
                        this,
                        ResolveReminderRegistry(runtime),
                        _timeProvider,
                        async () =>
                        {
                            TraceLog.Write("activation", $"activate {GrainId}");
                            if (_instance is IGrainLifecycleParticipant participant)
                            {
                                await participant.OnActivateAsync(turnToken);
                            }

                            return (object?)null;
                        });

                    return null;
                },
                CancellationToken.None);

            lock (_activationLock)
            {
                _activationState = ActivationCompletedState;
                _activationCompletion?.TrySetResult(true);
            }
        }
        catch (Exception exception)
        {
            var activationFailure = exception as ActivationInitializationException
                ?? new ActivationInitializationException(GrainId, innerException: exception);

            lock (_activationLock)
            {
                _activationState = ActivationFailedState;
                _activationCompletion?.TrySetException(activationFailure);
            }

            TraceLog.Write("activation", $"activate failed {GrainId}: {exception.Message}");
            throw activationFailure;
        }
    }

    private async ValueTask InvokeDeactivateAsync(ActivationDeactivationReason reason)
    {
        if (Volatile.Read(ref _activationState) != ActivationCompletedState
            || _instance is not IGrainLifecycleParticipant participant)
        {
            return;
        }

        var runtime = _activationRuntime;
        if (runtime is null)
        {
            return;
        }

        try
        {
            await ActivationExecutionContext.RunAsync(
                runtime,
                GrainId,
                Guid.NewGuid(),
                this,
                ResolveReminderRegistry(runtime),
                _timeProvider,
                async () =>
                {
                    await participant.OnDeactivateAsync(reason, CancellationToken.None);
                    return (object?)null;
                });
        }
        catch (Exception exception)
        {
            TraceLog.Write(
                "activation",
                $"deactivate hook failed {GrainId} reason={reason}: {exception.Message}");
        }
    }

    private ActivationInitializationException CreateActivationFailureException()
        => _activationCompletion?.Task.Exception?.InnerExceptions.OfType<ActivationInitializationException>().FirstOrDefault()
            ?? new ActivationInitializationException(GrainId);

    private void EnsureActiveForTimerRegistration()
    {
        if (Volatile.Read(ref _lifecycleState) != ActiveState)
        {
            throw new InvalidOperationException($"Activation '{GrainId}' is no longer active.");
        }
    }

    private void RemoveTimer(Guid timerId)
    {
        lock (_timerLock)
        {
            _timers.Remove(timerId);
        }
    }

    private IGrainReminderRegistry? ResolveReminderRegistry(IInvocationRuntime runtime)
        => runtime is IReminderRuntimeContext reminderRuntimeContext
            ? reminderRuntimeContext.GetReminderRegistry(GrainId)
            : null;

    private void Touch() => Interlocked.Exchange(ref _lastTouchedUtcTicks, _timeProvider.GetUtcNow().UtcTicks);
}
