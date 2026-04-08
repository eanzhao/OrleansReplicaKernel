namespace OrleansReplicaKernel.Runtime;

public sealed record MembershipDisseminationCheckpoint(
    int TickNumber,
    int NextFanoutStartIndex,
    IReadOnlyList<MembershipObserverCursor> Observers);
