using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Routing;

public interface IPlacementPolicy
{
    string SelectInitialOwner(
        GrainId grainId,
        IClusterMembershipView membershipView,
        PlacementLoadSnapshot loadSnapshot,
        GrainTypePlacementHint placementHint);
}
