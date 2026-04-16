using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Transactions;

internal sealed record TransactionParticipantReference(
    GrainId GrainId,
    string StateName,
    string? StorageName)
{
    public string ToStableKey()
        => $"{StorageName ?? "<default>"}\u001F{GrainId.GrainType}\u001F{GrainId.Key}\u001F{StateName}";
}
