using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Routing;

public interface IRebalancingPolicy
{
    string? SelectHandoffTarget(
        GrainOwnerRecord currentOwner,
        IClusterMembershipView membershipView,
        PlacementLoadSnapshot loadSnapshot);
}
