namespace OrleansReplicaKernel.Runtime;

public sealed class StaticClusterMembershipView : IClusterMembershipView
{
    private readonly IReadOnlyDictionary<string, NodeHealthStatus> _members;

    public StaticClusterMembershipView(
        string observerNodeName,
        IEnumerable<string> healthyNodeNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observerNodeName);
        ArgumentNullException.ThrowIfNull(healthyNodeNames);

        ObserverNodeName = observerNodeName;
        _members = healthyNodeNames
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                nodeName => nodeName,
                _ => NodeHealthStatus.Healthy,
                StringComparer.Ordinal);
    }

    public string ObserverNodeName { get; }

    public long CurrentEpoch => 0;

    public MembershipGossipTickResult ApplyGossip(IReadOnlyList<MembershipViewChange> changes)
        => new(0, 0);

    public MembershipGossipTickResult RunStabilizationTick()
        => new(0, 0);

    public NodeHealthStatus GetHealth(string nodeName)
        => _members.TryGetValue(nodeName, out var status)
            ? status
            : NodeHealthStatus.Unhealthy;

    public bool IsHealthy(string nodeName)
        => GetHealth(nodeName) == NodeHealthStatus.Healthy;

    public IReadOnlyList<string> GetHealthyMembers()
        => _members
            .Where(item => item.Value == NodeHealthStatus.Healthy)
            .Select(item => item.Key)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<ObservedClusterMemberRecord> GetMembers()
        => _members
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new ObservedClusterMemberRecord(
                item.Key,
                item.Value,
                item.Value,
                LastObservedEpoch: 0,
                LastObservedAtUtc: DateTimeOffset.UnixEpoch))
            .ToArray();
}
