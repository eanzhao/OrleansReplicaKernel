namespace OrleansReplicaKernel.Streaming;

public interface IGrainStreamRuntime
{
    IAsyncStream<T> GetStream<T>(string providerName, string namespaceName, string key);
}
