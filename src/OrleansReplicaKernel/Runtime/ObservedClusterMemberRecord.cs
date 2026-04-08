namespace OrleansReplicaKernel.Runtime;

public readonly record struct ObservedClusterMemberRecord(
    string NodeName,
    NodeHealthStatus StableStatus,
    NodeHealthStatus ObservedStatus,
    long LastObservedEpoch,
    DateTimeOffset LastObservedAtUtc);
