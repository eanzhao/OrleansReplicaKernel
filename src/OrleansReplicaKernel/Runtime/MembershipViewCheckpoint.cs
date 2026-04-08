namespace OrleansReplicaKernel.Runtime;

public sealed record MembershipViewCheckpoint(
    string ObserverNodeName,
    long CurrentEpoch,
    IReadOnlyList<ObservedClusterMemberRecord> Members);
