using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Storage;
using OrleansReplicaKernel.Streaming;

namespace OrleansReplicaKernel.Demo;

public sealed partial class StreamSubscriberGrain :
    IStreamSubscriberGrain,
    IAsyncStreamSubscriptionObserver<string>,
    IGrainLifecycleParticipant
{
    private readonly IPersistentState<StreamSubscriberState> _state;

    public StreamSubscriberGrain(
        [PersistentState("stream-subscriber")] IPersistentState<StreamSubscriberState> state)
    {
        _state = state;
    }

    public async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        await GrainStreams
            .GetStream<string>("memory", "demo", GetStreamKey())
            .SubscribeAsync(cancellationToken);
    }

    public ValueTask OnDeactivateAsync(
        ActivationDeactivationReason reason,
        CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public async ValueTask OnNextBatchAsync(StreamBatch<string> batch, CancellationToken cancellationToken = default)
    {
        _state.State.Received.AddRange(batch.Items);
        _state.State.BatchSizes.Add(batch.Items.Count);
        _state.State.LastSequenceToken = batch.EndSequenceToken;
        await _state.WriteStateAsync(cancellationToken);
    }

    public Task<string> GetReceivedSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_state.State.Received.Count == 0
            ? "<none>"
            : string.Join("|", _state.State.Received));

    public Task<string> GetBatchSizesSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_state.State.BatchSizes.Count == 0
            ? "<none>"
            : string.Join("|", _state.State.BatchSizes));

    public Task<long> GetLastSequenceTokenAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_state.State.LastSequenceToken);

    private static string GetStreamKey()
        => ActivationExecutionContext.CurrentGrainId?.Key
            ?? throw new InvalidOperationException("Stream subscriber requires an active grain id.");
}

public sealed class StreamSubscriberState
{
    public List<string> Received { get; set; } = [];

    public List<int> BatchSizes { get; set; } = [];

    public long LastSequenceToken { get; set; }
}
