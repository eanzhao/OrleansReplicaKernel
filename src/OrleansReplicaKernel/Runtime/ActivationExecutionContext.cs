using OrleansReplicaKernel.Invocation;

namespace OrleansReplicaKernel.Runtime;

public static class ActivationExecutionContext
{
    private static readonly AsyncLocal<IInvocationRuntime?> CurrentRuntimeSlot = new();

    public static IInvocationRuntime? CurrentRuntime => CurrentRuntimeSlot.Value;

    public static async ValueTask<T> RunAsync<T>(
        IInvocationRuntime runtime,
        Func<ValueTask<T>> callback)
    {
        var previousRuntime = CurrentRuntimeSlot.Value;
        CurrentRuntimeSlot.Value = runtime;

        try
        {
            return await callback();
        }
        finally
        {
            CurrentRuntimeSlot.Value = previousRuntime;
        }
    }
}
