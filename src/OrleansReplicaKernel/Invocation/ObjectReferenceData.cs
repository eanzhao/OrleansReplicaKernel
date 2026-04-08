using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Invocation;

public sealed record ObjectReferenceData(
    string InterfaceName,
    GrainId GrainId);
