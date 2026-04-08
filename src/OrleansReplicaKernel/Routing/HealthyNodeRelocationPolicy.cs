using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Routing;

public sealed class HealthyNodeRelocationPolicy : IOwnerRelocationPolicy
{
    private readonly string _preferredNodeName;

    public HealthyNodeRelocationPolicy(string preferredNodeName)
    {
        _preferredNodeName = preferredNodeName;
    }

    public string SelectOwner(
        GrainId grainId,
        string currentOwnerNodeName,
        IClusterMembershipView membershipView)
    {
        if (membershipView.IsHealthy(_preferredNodeName))
        {
            TraceLog.Write(
                "relocation",
                $"select preferred healthy owner {_preferredNodeName} for {grainId} after {currentOwnerNodeName} became unavailable at epoch={membershipView.CurrentEpoch}");
            return _preferredNodeName;
        }

        var fallback = membershipView.GetHealthyMembers().FirstOrDefault();
        if (fallback is not null)
        {
            TraceLog.Write(
                "relocation",
                $"select fallback healthy owner {fallback} for {grainId} after {currentOwnerNodeName} became unavailable at epoch={membershipView.CurrentEpoch}");
            return fallback;
        }

        throw new InvalidOperationException(
            $"No healthy membership candidate is available to relocate owner for grain '{grainId}'.");
    }
}
