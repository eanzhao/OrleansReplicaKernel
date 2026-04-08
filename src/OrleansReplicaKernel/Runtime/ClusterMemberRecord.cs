namespace OrleansReplicaKernel.Runtime;

public readonly record struct ClusterMemberRecord(
    string NodeName,
    NodeHealthStatus HealthStatus);
