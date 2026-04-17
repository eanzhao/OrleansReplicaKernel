using OrleansReplicaKernel.Storage;
using OrleansReplicaKernel.Streaming;

namespace OrleansReplicaKernel.Demo;

[ImplicitStreamSubscription("memory", "implicit")]
public sealed partial class ImplicitSubscriberGrain :
    IImplicitSubscriberGrain,
    IAsyncStreamSubscriptionObserver<string>
{
    private readonly IPersistentState<ImplicitSubscriberState> _state;

    public ImplicitSubscriberGrain(
        [PersistentState("implicit-subscriber")] IPersistentState<ImplicitSubscriberState> state)
    {
        _state = state;
    }

    public async ValueTask OnNextBatchAsync(StreamBatch<string> batch, CancellationToken cancellationToken = default)
    {
        _state.State.Received.AddRange(batch.Items);
        _state.State.LastSequenceToken = batch.EndSequenceToken;
        await _state.WriteStateAsync(cancellationToken);
    }

    public Task<string> GetReceivedSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_state.State.Received.Count == 0
            ? "<none>"
            : string.Join("|", _state.State.Received));

    public Task<long> GetLastSequenceTokenAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_state.State.LastSequenceToken);
}

public sealed class ImplicitSubscriberState
{
    public List<string> Received { get; set; } = [];

    public long LastSequenceToken { get; set; }
}
