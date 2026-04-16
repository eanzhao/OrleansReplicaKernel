using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Storage;

namespace OrleansReplicaKernel.Streaming;

public interface IPubSubRendezvousGrain
{
    Task<StreamSubscriptionState> RegisterSubscriptionAsync(
        GrainId subscriberGrainId,
        string payloadTypeName,
        long initialSequenceToken,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    Task<bool> UnregisterSubscriptionAsync(
        GrainId subscriberGrainId,
        CancellationToken cancellationToken = default);

    Task<StreamSubscriptionState[]> GetSubscriptionsAsync(CancellationToken cancellationToken = default);

    Task<bool> CommitBatchAsync(
        Guid subscriptionId,
        long expectedSequenceToken,
        long nextSequenceToken,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);
}

public sealed partial class PubSubRendezvousGrain : IPubSubRendezvousGrain
{
    private readonly IPersistentState<PubSubRendezvousState> _state;

    public PubSubRendezvousGrain(
        [PersistentState("subscriptions")] IPersistentState<PubSubRendezvousState> state)
    {
        _state = state;
    }

    public async Task<StreamSubscriptionState> RegisterSubscriptionAsync(
        GrainId subscriberGrainId,
        string payloadTypeName,
        long initialSequenceToken,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadTypeName);
        if (initialSequenceToken <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialSequenceToken),
                "Initial stream sequence token must be positive.");
        }

        if (!string.IsNullOrEmpty(_state.State.PayloadTypeName)
            && !string.Equals(_state.State.PayloadTypeName, payloadTypeName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Pub/Sub subscription payload type mismatch. Existing='{_state.State.PayloadTypeName}', requested='{payloadTypeName}'.");
        }

        _state.State.PayloadTypeName = payloadTypeName;

        var existingIndex = _state.State.Subscriptions.FindIndex(item => item.SubscriberGrainId == subscriberGrainId);
        if (existingIndex >= 0)
        {
            return _state.State.Subscriptions[existingIndex];
        }

        var created = new StreamSubscriptionState(
            Guid.NewGuid(),
            subscriberGrainId,
            initialSequenceToken,
            utcNow);
        _state.State.Subscriptions.Add(created);
        SortSubscriptions(_state.State.Subscriptions);
        await _state.WriteStateAsync(cancellationToken);
        return created;
    }

    public async Task<bool> UnregisterSubscriptionAsync(
        GrainId subscriberGrainId,
        CancellationToken cancellationToken = default)
    {
        var removed = _state.State.Subscriptions.RemoveAll(item => item.SubscriberGrainId == subscriberGrainId);
        if (removed == 0)
        {
            return false;
        }

        await _state.WriteStateAsync(cancellationToken);
        return true;
    }

    public Task<StreamSubscriptionState[]> GetSubscriptionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(
            _state.State.Subscriptions
                .OrderBy(item => item.SubscriberGrainId.ToString(), StringComparer.Ordinal)
                .ToArray());

    public async Task<bool> CommitBatchAsync(
        Guid subscriptionId,
        long expectedSequenceToken,
        long nextSequenceToken,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        if (expectedSequenceToken <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedSequenceToken),
                "Expected stream sequence token must be positive.");
        }

        if (nextSequenceToken < expectedSequenceToken)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextSequenceToken),
                "Committed stream sequence token must not move backwards.");
        }

        var existingIndex = _state.State.Subscriptions.FindIndex(item => item.SubscriptionId == subscriptionId);
        if (existingIndex < 0)
        {
            return false;
        }

        var existing = _state.State.Subscriptions[existingIndex];
        if (existing.NextSequenceToken != expectedSequenceToken)
        {
            return false;
        }

        _state.State.Subscriptions[existingIndex] = existing with
        {
            NextSequenceToken = nextSequenceToken,
            UpdatedUtc = utcNow
        };
        SortSubscriptions(_state.State.Subscriptions);
        await _state.WriteStateAsync(cancellationToken);
        return true;
    }

    private static void SortSubscriptions(List<StreamSubscriptionState> subscriptions)
        => subscriptions.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(
                left.SubscriberGrainId.ToString(),
                right.SubscriberGrainId.ToString()));
}

public sealed class PubSubRendezvousState
{
    public string PayloadTypeName { get; set; } = string.Empty;

    public List<StreamSubscriptionState> Subscriptions { get; set; } = [];
}
