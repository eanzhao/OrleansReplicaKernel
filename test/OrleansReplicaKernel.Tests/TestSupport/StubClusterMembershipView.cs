using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Tests.TestSupport;

internal sealed class StubClusterMembershipView : IClusterMembershipView
{
    private readonly Dictionary<string, NodeHealthStatus> _members;

    public StubClusterMembershipView(
        IEnumerable<KeyValuePair<string, NodeHealthStatus>> members,
        long currentEpoch = 1,
        string observerNodeName = "observer")
    {
        _members = members.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        CurrentEpoch = currentEpoch;
        ObserverNodeName = observerNodeName;
    }

    public string ObserverNodeName { get; }

    public long CurrentEpoch { get; private set; }

    public MembershipGossipTickResult ApplyGossip(IReadOnlyList<MembershipViewChange> changes)
    {
        foreach (var change in changes.OrderBy(item => item.Epoch))
        {
            _members[change.NodeName] = change.CurrentStatus;
            CurrentEpoch = Math.Max(CurrentEpoch, change.Epoch);
        }

        return new MembershipGossipTickResult(changes.Count, 0);
    }

    public MembershipGossipTickResult RunStabilizationTick() => new(0, 0);

    public NodeHealthStatus GetHealth(string nodeName)
        => _members.TryGetValue(nodeName, out var status)
            ? status
            : NodeHealthStatus.Unhealthy;

    public bool IsHealthy(string nodeName) => GetHealth(nodeName) == NodeHealthStatus.Healthy;

    public IReadOnlyList<string> GetHealthyMembers()
        => _members
            .Where(pair => pair.Value == NodeHealthStatus.Healthy)
            .Select(pair => pair.Key)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<ObservedClusterMemberRecord> GetMembers()
        => _members
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ObservedClusterMemberRecord(
                pair.Key,
                pair.Value,
                pair.Value,
                CurrentEpoch,
                DateTimeOffset.UnixEpoch))
            .ToArray();

    public void SetHealth(string nodeName, NodeHealthStatus status)
    {
        _members[nodeName] = status;
    }
}
