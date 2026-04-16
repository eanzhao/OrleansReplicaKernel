using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Routing;

internal sealed class FilteredClusterMembershipView : IClusterMembershipView
{
    private readonly IClusterMembershipView _inner;
    private readonly HashSet<string> _allowedNodeNames;

    public FilteredClusterMembershipView(IClusterMembershipView inner, IEnumerable<string> allowedNodeNames)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(allowedNodeNames);
        _allowedNodeNames = allowedNodeNames.ToHashSet(StringComparer.Ordinal);
    }

    public string ObserverNodeName => _inner.ObserverNodeName;

    public long CurrentEpoch => _inner.CurrentEpoch;

    public MembershipGossipTickResult ApplyGossip(IReadOnlyList<MembershipViewChange> changes)
        => throw new NotSupportedException("Filtered membership views are read-only routing helpers.");

    public MembershipGossipTickResult RunStabilizationTick()
        => throw new NotSupportedException("Filtered membership views are read-only routing helpers.");

    public NodeHealthStatus GetHealth(string nodeName)
        => _allowedNodeNames.Contains(nodeName)
            ? _inner.GetHealth(nodeName)
            : NodeHealthStatus.Unhealthy;

    public bool IsHealthy(string nodeName)
        => _allowedNodeNames.Contains(nodeName)
           && _inner.IsHealthy(nodeName);

    public IReadOnlyList<string> GetHealthyMembers()
        => _inner.GetHealthyMembers()
            .Where(nodeName => _allowedNodeNames.Contains(nodeName))
            .OrderBy(nodeName => nodeName, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<ObservedClusterMemberRecord> GetMembers()
        => _inner.GetMembers()
            .Where(item => _allowedNodeNames.Contains(item.NodeName))
            .OrderBy(item => item.NodeName, StringComparer.Ordinal)
            .ToArray();
}
