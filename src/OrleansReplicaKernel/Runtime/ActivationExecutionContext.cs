using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Reminders;
using OrleansReplicaKernel.Security;
using OrleansReplicaKernel.Streaming;
using OrleansReplicaKernel.Transactions;

namespace OrleansReplicaKernel.Runtime;

public static class ActivationExecutionContext
{
    private static readonly AsyncLocal<ExecutionState?> CurrentStateSlot = new();
    private static readonly AsyncLocal<TimeSpan?> RequestTimeoutSlot = new();

    public static IInvocationRuntime? CurrentRuntime => CurrentStateSlot.Value?.Runtime;

    public static Guid? CurrentRequestChainId => CurrentStateSlot.Value?.RequestChainId;

    public static GrainId? CurrentGrainId => CurrentStateSlot.Value?.GrainId;

    public static IActivationTimerRegistry? CurrentTimerRegistry => CurrentStateSlot.Value?.TimerRegistry;

    public static IGrainReminderRegistry? CurrentReminderRegistry => CurrentStateSlot.Value?.ReminderRegistry;

    public static IGrainStreamRuntime? CurrentStreamRuntime => CurrentStateSlot.Value?.StreamRuntime;

    public static TimeProvider? CurrentTimeProvider => CurrentStateSlot.Value?.TimeProvider;

    public static InvocationIdentity? CurrentInvocationIdentity => CurrentStateSlot.Value?.Identity;

    public static TransactionInfo? CurrentTransaction => TransactionContext.Current;

    public static TimeSpan? CurrentRequestTimeout
    {
        get => RequestTimeoutSlot.Value;
        set => RequestTimeoutSlot.Value = value;
    }

    public static async ValueTask<T> RunAsync<T>(
        IInvocationRuntime runtime,
        GrainId grainId,
        Guid requestChainId,
        IActivationTimerRegistry timerRegistry,
        IGrainReminderRegistry? reminderRegistry,
        IGrainStreamRuntime? streamRuntime,
        TimeProvider timeProvider,
        InvocationIdentity? identity,
        TransactionInfo? transaction,
        Func<ValueTask<T>> callback)
    {
        var previousState = CurrentStateSlot.Value;
        using var transactionScope = TransactionContext.Enter(transaction);
        CurrentStateSlot.Value = new ExecutionState(
            runtime,
            grainId,
            requestChainId,
            timerRegistry,
            reminderRegistry,
            streamRuntime,
            timeProvider,
            identity);

        try
        {
            return await callback();
        }
        finally
        {
            CurrentStateSlot.Value = previousState;
        }
    }

    private sealed record ExecutionState(
        IInvocationRuntime Runtime,
        GrainId GrainId,
        Guid RequestChainId,
        IActivationTimerRegistry TimerRegistry,
        IGrainReminderRegistry? ReminderRegistry,
        IGrainStreamRuntime? StreamRuntime,
        TimeProvider TimeProvider,
        InvocationIdentity? Identity);
}
