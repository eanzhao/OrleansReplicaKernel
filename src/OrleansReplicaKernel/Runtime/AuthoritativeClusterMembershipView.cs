namespace OrleansReplicaKernel.Runtime;

public sealed class AuthoritativeClusterMembershipView : IClusterMembershipView
{
    private readonly IClusterMembership _membership;
    private readonly TimeProvider _timeProvider;

    public AuthoritativeClusterMembershipView(
        string observerNodeName,
        IClusterMembership membership,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observerNodeName);
        _membership = membership ?? throw new ArgumentNullException(nameof(membership));
        _timeProvider = timeProvider ?? TimeProvider.System;
        ObserverNodeName = observerNodeName;
    }

    public string ObserverNodeName { get; }

    public long CurrentEpoch => _membership.CurrentEpoch;

    public MembershipGossipTickResult ApplyGossip(IReadOnlyList<MembershipViewChange> changes) => new(0, 0);

    public MembershipGossipTickResult RunStabilizationTick() => new(0, 0);

    public NodeHealthStatus GetHealth(string nodeName) => _membership.GetHealth(nodeName);

    public bool IsHealthy(string nodeName) => _membership.IsHealthy(nodeName);

    public IReadOnlyList<string> GetHealthyMembers() => _membership.GetHealthyMembers();

    public IReadOnlyList<ObservedClusterMemberRecord> GetMembers()
    {
        var observedAtUtc = _timeProvider.GetUtcNow();
        return _membership.GetMembers()
            .OrderBy(item => item.NodeName, StringComparer.Ordinal)
            .Select(item => new ObservedClusterMemberRecord(
                item.NodeName,
                item.HealthStatus,
                item.HealthStatus,
                _membership.CurrentEpoch,
                observedAtUtc))
            .ToArray();
    }
}
