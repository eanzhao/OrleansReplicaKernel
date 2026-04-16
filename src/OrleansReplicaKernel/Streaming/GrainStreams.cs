using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Streaming;

public static class GrainStreams
{
    public static IAsyncStream<T> GetStream<T>(
        string providerName,
        string namespaceName,
        string key)
    {
        var runtime = ActivationExecutionContext.CurrentStreamRuntime
            ?? throw new InvalidOperationException("Stream access requires an active grain execution context.");
        return runtime.GetStream<T>(providerName, namespaceName, key);
    }
}
