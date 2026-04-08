using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Routing;

public sealed class LeastLoadedPlacementPolicy : IPlacementPolicy
{
    private readonly string _preferredNodeName;

    public LeastLoadedPlacementPolicy(string preferredNodeName)
    {
        _preferredNodeName = preferredNodeName;
    }

    public string SelectInitialOwner(
        GrainId grainId,
        IClusterMembershipView membershipView,
        PlacementLoadSnapshot loadSnapshot)
    {
        var candidates = membershipView.GetHealthyMembers()
            .Select(nodeName => new ActivationLoadRecord(nodeName, loadSnapshot.GetActivationCount(nodeName)))
            .OrderBy(item => item.ActivationCount)
            .ThenBy(item => string.Equals(item.NodeName, _preferredNodeName, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(item => item.NodeName, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException($"No healthy placement candidate is available for grain '{grainId}'.");
        }

        var selected = candidates[0];
        TraceLog.Write(
            "placement",
            $"select initial owner {selected.NodeName} for {grainId} load={selected.ActivationCount}");
        return selected.NodeName;
    }
}
