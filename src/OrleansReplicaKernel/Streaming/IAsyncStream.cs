namespace OrleansReplicaKernel.Streaming;

public interface IAsyncStream<T>
{
    ValueTask OnNextAsync(T item, CancellationToken cancellationToken = default);

    ValueTask<StreamSubscriptionState> SubscribeAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> UnsubscribeAsync(CancellationToken cancellationToken = default);
}
