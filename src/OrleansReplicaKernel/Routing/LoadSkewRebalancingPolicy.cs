using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Routing;

public sealed class LoadSkewRebalancingPolicy : IRebalancingPolicy
{
    private readonly int _minimumSkew;

    public LoadSkewRebalancingPolicy(int minimumSkew)
    {
        _minimumSkew = Math.Max(1, minimumSkew);
    }

    public string? SelectHandoffTarget(
        GrainOwnerRecord currentOwner,
        IClusterMembershipView membershipView,
        PlacementLoadSnapshot loadSnapshot)
    {
        if (!membershipView.IsHealthy(currentOwner.OwnerNodeName))
        {
            return null;
        }

        var currentLoad = loadSnapshot.GetActivationCount(currentOwner.OwnerNodeName);
        var target = membershipView.GetHealthyMembers()
            .Where(nodeName => !string.Equals(nodeName, currentOwner.OwnerNodeName, StringComparison.Ordinal))
            .Select(nodeName => new ActivationLoadRecord(nodeName, loadSnapshot.GetActivationCount(nodeName)))
            .OrderBy(item => item.ActivationCount)
            .ThenBy(item => item.NodeName, StringComparer.Ordinal)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(target.NodeName))
        {
            return null;
        }

        var skew = currentLoad - target.ActivationCount;
        if (skew < _minimumSkew)
        {
            return null;
        }

        TraceLog.Write(
            "rebalancing",
            $"select handoff target {target.NodeName} for {currentOwner.GrainId} current-load={currentLoad} target-load={target.ActivationCount} skew={skew}");
        return target.NodeName;
    }
}
