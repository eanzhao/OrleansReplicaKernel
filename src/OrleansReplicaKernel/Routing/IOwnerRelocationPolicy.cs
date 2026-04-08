using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Routing;

public interface IOwnerRelocationPolicy
{
    string SelectOwner(
        GrainId grainId,
        string currentOwnerNodeName,
        IClusterMembershipView membershipView);
}
