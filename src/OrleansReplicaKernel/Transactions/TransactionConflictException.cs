using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Transactions;

public sealed class TransactionConflictException : Exception
{
    public TransactionConflictException(
        Guid transactionId,
        GrainId grainId,
        string stateName,
        Guid? ownerTransactionId = null,
        Exception? innerException = null)
        : base(
            ownerTransactionId is null
                ? $"Transaction '{transactionId:N}' conflicted while accessing '{grainId}' state '{stateName}'."
                : $"Transaction '{transactionId:N}' conflicted with transaction '{ownerTransactionId:N}' while accessing '{grainId}' state '{stateName}'.",
            innerException)
    {
        TransactionId = transactionId;
        GrainId = grainId;
        StateName = stateName;
        OwnerTransactionId = ownerTransactionId;
    }

    public Guid TransactionId { get; }

    public GrainId GrainId { get; }

    public string StateName { get; }

    public Guid? OwnerTransactionId { get; }
}
