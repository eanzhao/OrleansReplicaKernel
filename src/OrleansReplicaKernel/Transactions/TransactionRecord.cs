namespace OrleansReplicaKernel.Transactions;

internal sealed class TransactionRecord
{
    public Guid TransactionId { get; set; }

    public TransactionStatus Status { get; set; }

    public List<TransactionParticipantReference> Participants { get; set; } = [];

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}
