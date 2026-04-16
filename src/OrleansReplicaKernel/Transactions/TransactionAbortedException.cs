namespace OrleansReplicaKernel.Transactions;

public sealed class TransactionAbortedException : Exception
{
    public TransactionAbortedException(Guid transactionId, string? reason = null, Exception? innerException = null)
        : base(
            string.IsNullOrWhiteSpace(reason)
                ? $"Transaction '{transactionId:N}' was aborted."
                : $"Transaction '{transactionId:N}' was aborted: {reason}",
            innerException)
    {
        TransactionId = transactionId;
    }

    public Guid TransactionId { get; }
}
