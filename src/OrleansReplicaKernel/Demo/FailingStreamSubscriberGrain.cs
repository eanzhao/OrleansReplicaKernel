using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Storage;
using OrleansReplicaKernel.Streaming;

namespace OrleansReplicaKernel.Demo;

public sealed partial class FailingStreamSubscriberGrain :
    IFailingStreamSubscriberGrain,
    IAsyncStreamSubscriptionObserver<string>,
    IGrainLifecycleParticipant
{
    private readonly IPersistentState<FailingSubscriberState> _state;

    public FailingStreamSubscriberGrain(
        [PersistentState("failing-subscriber")] IPersistentState<FailingSubscriberState> state)
    {
        _state = state;
    }

    public async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        await GrainStreams
            .GetStream<string>("memory", "failing", GetStreamKey())
            .SubscribeAsync(cancellationToken);
    }

    public ValueTask OnDeactivateAsync(
        ActivationDeactivationReason reason,
        CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public async ValueTask OnNextBatchAsync(StreamBatch<string> batch, CancellationToken cancellationToken = default)
    {
        _state.State.DeliveryAttemptCount++;
        await _state.WriteStateAsync(cancellationToken);
        throw new InvalidOperationException(
            $"Simulated delivery failure (attempt {_state.State.DeliveryAttemptCount})");
    }

    public Task<int> GetDeliveryAttemptCountAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_state.State.DeliveryAttemptCount);

    private static string GetStreamKey()
        => ActivationExecutionContext.CurrentGrainId?.Key
            ?? throw new InvalidOperationException("Stream subscriber requires an active grain id.");
}

public sealed class FailingSubscriberState
{
    public int DeliveryAttemptCount { get; set; }
}
