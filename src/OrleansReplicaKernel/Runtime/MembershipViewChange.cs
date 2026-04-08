namespace OrleansReplicaKernel.Runtime;

public sealed record MembershipViewChange(
    long Epoch,
    string NodeName,
    NodeHealthStatus? PreviousStatus,
    NodeHealthStatus CurrentStatus,
    string Reason,
    DateTimeOffset CreatedAtUtc);
