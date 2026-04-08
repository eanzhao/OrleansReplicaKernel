using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Routing;

public sealed record ActivationMetadataRecord(
    GrainId GrainId,
    string InstanceTypeName,
    DateTimeOffset LastTouchedUtc,
    long OwnerVersion);
