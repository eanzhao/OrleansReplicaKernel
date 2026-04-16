namespace OrleansReplicaKernel.Streaming;

public interface IAsyncStreamSubscriptionObserver<T>
{
    ValueTask OnNextBatchAsync(StreamBatch<T> batch, CancellationToken cancellationToken = default);
}
