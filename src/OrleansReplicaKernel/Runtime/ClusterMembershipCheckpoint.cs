namespace OrleansReplicaKernel.Runtime;

public sealed record ClusterMembershipCheckpoint(
    long CurrentEpoch,
    IReadOnlyList<ClusterMemberRecord> Members,
    IReadOnlyList<MembershipViewChange> ViewChanges);
