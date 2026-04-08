using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Invocation;

public interface IInvocationRuntime
{
    ValueTask<TResult> InvokeAsync<TResult>(
        GrainId grainId,
        IInvokable invokable,
        CancellationToken cancellationToken = default);
}
