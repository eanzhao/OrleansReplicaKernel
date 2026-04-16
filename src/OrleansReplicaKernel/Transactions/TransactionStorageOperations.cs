using System.Text;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Storage;

namespace OrleansReplicaKernel.Transactions;

internal static class TransactionStorageOperations
{
    public const string TransactionStateName = "transaction";
    public const string ParticipantStateName = "participant";

    public static GrainId CreateTransactionRecordGrainId(Guid transactionId)
        => new(TransactionsGrainTypes.TransactionAgent, transactionId.ToString("N"));

    public static GrainId CreateParticipantRecordGrainId(TransactionParticipantReference participant)
    {
        var rawKey = string.Join(
            "\u001F",
            participant.GrainId.GrainType,
            participant.GrainId.Key,
            participant.StateName,
            participant.StorageName ?? string.Empty);
        var encodedKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawKey));
        return new GrainId(TransactionsGrainTypes.TransactionalStateParticipant, encodedKey);
    }

    public static async ValueTask<GrainState<TRecord>> ReadRecordAsync<TRecord>(
        IGrainStorage storage,
        string stateName,
        GrainId grainId,
        CancellationToken cancellationToken = default)
    {
        var grainState = new GrainState<TRecord>();
        await storage.ReadStateAsync(stateName, grainId, grainState, cancellationToken);
        return grainState;
    }

    public static bool IsWriteConflict(Exception exception)
        => exception is InconsistentStateException;
}
