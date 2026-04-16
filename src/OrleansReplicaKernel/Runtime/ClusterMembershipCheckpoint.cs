namespace OrleansReplicaKernel.Runtime;

public sealed record ClusterMembershipCheckpoint(
    long CurrentEpoch,
    IReadOnlyList<ClusterMemberRecord> Members,
    IReadOnlyList<MembershipViewChange> ViewChanges)
{
    public static ClusterMembershipCheckpoint Empty { get; } = new(
        CurrentEpoch: 0,
        Members: [],
        ViewChanges: []);
}
