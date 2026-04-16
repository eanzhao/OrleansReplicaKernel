using System.Text.Json;

namespace OrleansReplicaKernel.Transactions;

internal sealed class TransactionalStateParticipantRecord
{
    public long CommittedVersion { get; set; }

    public JsonElement? CommittedState { get; set; }

    public Guid? LockedTransactionId { get; set; }

    public TransactionParticipantWrite? PendingWrite { get; set; }
}

internal sealed class TransactionParticipantWrite
{
    public Guid TransactionId { get; set; }

    public JsonElement State { get; set; }

    public long BaseVersion { get; set; }

    public TransactionParticipantWriteStatus Status { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}
