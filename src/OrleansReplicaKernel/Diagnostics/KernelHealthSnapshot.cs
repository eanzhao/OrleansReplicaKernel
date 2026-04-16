using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Diagnostics;

public sealed record KernelHealthSnapshot(
    string PrimaryNodeName,
    DateTimeOffset CapturedUtc,
    long MembershipEpoch,
    bool IsHealthy,
    IReadOnlyList<KernelNodeHealthSnapshot> Nodes);

public sealed record KernelNodeHealthSnapshot(
    string NodeName,
    NodeHealthStatus HealthStatus,
    int ActivationCount,
    ResponseDispositionSnapshot ResponseDisposition);
