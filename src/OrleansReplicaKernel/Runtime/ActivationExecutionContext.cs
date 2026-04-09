using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Runtime;

public static class ActivationExecutionContext
{
    private static readonly AsyncLocal<ExecutionState?> CurrentStateSlot = new();

    public static IInvocationRuntime? CurrentRuntime => CurrentStateSlot.Value?.Runtime;

    public static Guid? CurrentRequestChainId => CurrentStateSlot.Value?.RequestChainId;

    public static GrainId? CurrentGrainId => CurrentStateSlot.Value?.GrainId;

    public static async ValueTask<T> RunAsync<T>(
        IInvocationRuntime runtime,
        GrainId grainId,
        Guid requestChainId,
        Func<ValueTask<T>> callback)
    {
        var previousState = CurrentStateSlot.Value;
        CurrentStateSlot.Value = new ExecutionState(runtime, grainId, requestChainId);

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
        Guid RequestChainId);
}
