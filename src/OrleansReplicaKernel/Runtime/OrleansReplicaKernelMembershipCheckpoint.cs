namespace OrleansReplicaKernel.Runtime;

public sealed record OrleansReplicaKernelMembershipCheckpoint(
    ClusterMembershipCheckpoint ClusterMembership,
    IReadOnlyList<MembershipViewCheckpoint> Views,
    MembershipDisseminationCheckpoint Dissemination);
