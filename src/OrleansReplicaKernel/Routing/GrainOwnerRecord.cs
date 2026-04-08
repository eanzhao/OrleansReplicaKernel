using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Routing;

public readonly record struct GrainOwnerRecord(
    GrainId GrainId,
    string OwnerNodeName,
    long Version);
