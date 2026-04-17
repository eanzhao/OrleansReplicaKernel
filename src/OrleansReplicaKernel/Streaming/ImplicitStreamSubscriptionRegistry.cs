using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Streaming;

public sealed class ImplicitStreamSubscriptionRegistry
{
    private readonly Dictionary<(string ProviderName, string StreamNamespace), List<string>> _subscriptions = new();

    public void Register(string providerName, string streamNamespace, string grainType)
    {
        var key = (providerName, streamNamespace);
        if (!_subscriptions.TryGetValue(key, out var grainTypes))
        {
            grainTypes = [];
            _subscriptions[key] = grainTypes;
        }

        if (!grainTypes.Contains(grainType))
        {
            grainTypes.Add(grainType);
        }
    }

    public IReadOnlyList<GrainId> GetImplicitSubscribers(StreamId streamId)
    {
        var key = (streamId.ProviderName, streamId.Namespace);
        if (!_subscriptions.TryGetValue(key, out var grainTypes))
        {
            return [];
        }

        return grainTypes.Select(gt => new GrainId(gt, streamId.Key)).ToArray();
    }

    public bool HasImplicitSubscribers(string providerName, string streamNamespace)
        => _subscriptions.ContainsKey((providerName, streamNamespace));
}
