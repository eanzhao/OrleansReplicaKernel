using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Storage;

namespace OrleansReplicaKernel.Transactions;

internal sealed class TransactionCoordinator
{
    public static TransactionCoordinator Disabled { get; } = new(GrainStorageResolver.Empty, TimeProvider.System);

    private readonly GrainStorageResolver _storageResolver;
    private readonly TimeProvider _timeProvider;

    public TransactionCoordinator(GrainStorageResolver storageResolver, TimeProvider timeProvider)
    {
        _storageResolver = storageResolver ?? throw new ArgumentNullException(nameof(storageResolver));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask StartAsync(TransactionInfo transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var storage = ResolveTransactionLogStorage();
        var grainId = TransactionStorageOperations.CreateTransactionRecordGrainId(transaction.TransactionId);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = await TransactionStorageOperations.ReadRecordAsync<TransactionRecord>(
                storage,
                TransactionStorageOperations.TransactionStateName,
                grainId,
                cancellationToken);
            if (state.RecordExists)
            {
                if (state.State.Status == TransactionStatus.Aborted)
                {
                    throw new TransactionAbortedException(transaction.TransactionId);
                }

                return;
            }

            var utcNow = _timeProvider.GetUtcNow();
            state.State = new TransactionRecord
            {
                TransactionId = transaction.TransactionId,
                Status = TransactionStatus.Active,
                CreatedUtc = utcNow,
                UpdatedUtc = utcNow
            };

            try
            {
                await storage.WriteStateAsync(
                    TransactionStorageOperations.TransactionStateName,
                    grainId,
                    state,
                    cancellationToken);
                TraceLog.Write("transaction", $"start {transaction.TransactionId:N}");
                return;
            }
            catch (Exception exception) when (TransactionStorageOperations.IsWriteConflict(exception))
            {
            }
        }
    }

    public async ValueTask RegisterParticipantAsync(
        Guid transactionId,
        TransactionParticipantReference participant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(participant);

        var storage = ResolveTransactionLogStorage();
        var grainId = TransactionStorageOperations.CreateTransactionRecordGrainId(transactionId);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = await TransactionStorageOperations.ReadRecordAsync<TransactionRecord>(
                storage,
                TransactionStorageOperations.TransactionStateName,
                grainId,
                cancellationToken);
            if (!state.RecordExists)
            {
                throw new TransactionAbortedException(transactionId, "transaction log record is missing");
            }

            if (state.State.Status == TransactionStatus.Aborted)
            {
                throw new TransactionAbortedException(transactionId);
            }

            if (state.State.Status == TransactionStatus.Committed)
            {
                throw new TransactionAbortedException(transactionId, "transaction is already committing or committed");
            }

            if (state.State.Participants.Any(existing => existing == participant))
            {
                return;
            }

            state.State.Participants.Add(participant);
            state.State.Participants = state.State.Participants
                .OrderBy(item => item.ToStableKey(), StringComparer.Ordinal)
                .ToList();
            state.State.UpdatedUtc = _timeProvider.GetUtcNow();

            try
            {
                await storage.WriteStateAsync(
                    TransactionStorageOperations.TransactionStateName,
                    grainId,
                    state,
                    cancellationToken);
                TraceLog.Write(
                    "transaction",
                    $"register participant tx={transactionId:N} target={participant.GrainId} state={participant.StateName}");
                return;
            }
            catch (Exception exception) when (TransactionStorageOperations.IsWriteConflict(exception))
            {
            }
        }
    }

    public async ValueTask CommitAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        var record = await ReadTransactionRecordRequiredAsync(transactionId, cancellationToken);
        if (record.Status == TransactionStatus.Committed)
        {
            return;
        }

        if (record.Status == TransactionStatus.Aborted)
        {
            throw new TransactionAbortedException(transactionId);
        }

        try
        {
            foreach (var participant in record.Participants)
            {
                await PrepareParticipantAsync(transactionId, participant, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is TransactionConflictException or TransactionAbortedException)
        {
            await MarkStatusAsync(transactionId, TransactionStatus.Aborted, cancellationToken);
            await AbortParticipantsAsync(record.Participants, transactionId, cancellationToken);
            throw;
        }
        catch
        {
            await MarkStatusAsync(transactionId, TransactionStatus.Aborted, cancellationToken);
            await AbortParticipantsAsync(record.Participants, transactionId, cancellationToken);
            throw;
        }

        await MarkStatusAsync(transactionId, TransactionStatus.Committed, cancellationToken);

        foreach (var participant in record.Participants)
        {
            await CommitParticipantAsync(transactionId, participant, cancellationToken);
        }

        TraceLog.Write("transaction", $"commit {transactionId:N}");
    }

    public async ValueTask AbortAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        var record = await TryReadTransactionRecordAsync(transactionId, cancellationToken);
        if (record is null)
        {
            return;
        }

        if (record.Status != TransactionStatus.Committed)
        {
            await MarkStatusAsync(transactionId, TransactionStatus.Aborted, cancellationToken);
            await AbortParticipantsAsync(record.Participants, transactionId, cancellationToken);
            TraceLog.Write("transaction", $"abort {transactionId:N}");
        }
    }

    public async ValueTask<TransactionStatus?> GetStatusAsync(
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        var record = await TryReadTransactionRecordAsync(transactionId, cancellationToken);
        return record?.Status;
    }

    public async ValueTask ResolveParticipantAsync(
        TransactionParticipantReference participant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(participant);

        var storage = ResolveParticipantStorage(participant);
        var grainId = TransactionStorageOperations.CreateParticipantRecordGrainId(participant);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = await TransactionStorageOperations.ReadRecordAsync<TransactionalStateParticipantRecord>(
                storage,
                TransactionStorageOperations.ParticipantStateName,
                grainId,
                cancellationToken);
            if (!state.RecordExists
                || state.State.LockedTransactionId is null
                || state.State.PendingWrite is null)
            {
                return;
            }

            var transactionId = state.State.LockedTransactionId.Value;
            var decision = await GetStatusAsync(transactionId, cancellationToken);
            if (decision == TransactionStatus.Active)
            {
                return;
            }

            if (decision == TransactionStatus.Committed)
            {
                state.State.CommittedState = state.State.PendingWrite.State;
                state.State.CommittedVersion++;
            }

            state.State.LockedTransactionId = null;
            state.State.PendingWrite = null;

            try
            {
                await storage.WriteStateAsync(
                    TransactionStorageOperations.ParticipantStateName,
                    grainId,
                    state,
                    cancellationToken);
                return;
            }
            catch (Exception exception) when (TransactionStorageOperations.IsWriteConflict(exception))
            {
            }
        }
    }

    private async ValueTask PrepareParticipantAsync(
        Guid transactionId,
        TransactionParticipantReference participant,
        CancellationToken cancellationToken)
    {
        var storage = ResolveParticipantStorage(participant);
        var grainId = TransactionStorageOperations.CreateParticipantRecordGrainId(participant);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = await TransactionStorageOperations.ReadRecordAsync<TransactionalStateParticipantRecord>(
                storage,
                TransactionStorageOperations.ParticipantStateName,
                grainId,
                cancellationToken);
            if (!state.RecordExists
                || state.State.LockedTransactionId != transactionId
                || state.State.PendingWrite is null)
            {
                return;
            }

            if (state.State.PendingWrite.TransactionId != transactionId)
            {
                throw new TransactionConflictException(
                    transactionId,
                    participant.GrainId,
                    participant.StateName,
                    state.State.PendingWrite.TransactionId);
            }

            if (state.State.PendingWrite.Status == TransactionParticipantWriteStatus.Prepared)
            {
                return;
            }

            state.State.PendingWrite.Status = TransactionParticipantWriteStatus.Prepared;
            state.State.PendingWrite.UpdatedUtc = _timeProvider.GetUtcNow();

            try
            {
                await storage.WriteStateAsync(
                    TransactionStorageOperations.ParticipantStateName,
                    grainId,
                    state,
                    cancellationToken);
                return;
            }
            catch (Exception exception) when (TransactionStorageOperations.IsWriteConflict(exception))
            {
            }
        }
    }

    private async ValueTask CommitParticipantAsync(
        Guid transactionId,
        TransactionParticipantReference participant,
        CancellationToken cancellationToken)
    {
        var storage = ResolveParticipantStorage(participant);
        var grainId = TransactionStorageOperations.CreateParticipantRecordGrainId(participant);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = await TransactionStorageOperations.ReadRecordAsync<TransactionalStateParticipantRecord>(
                storage,
                TransactionStorageOperations.ParticipantStateName,
                grainId,
                cancellationToken);
            if (!state.RecordExists
                || state.State.LockedTransactionId != transactionId
                || state.State.PendingWrite is null)
            {
                return;
            }

            state.State.CommittedState = state.State.PendingWrite.State;
            state.State.CommittedVersion++;
            state.State.LockedTransactionId = null;
            state.State.PendingWrite = null;

            try
            {
                await storage.WriteStateAsync(
                    TransactionStorageOperations.ParticipantStateName,
                    grainId,
                    state,
                    cancellationToken);
                return;
            }
            catch (Exception exception) when (TransactionStorageOperations.IsWriteConflict(exception))
            {
            }
        }
    }

    private async ValueTask AbortParticipantAsync(
        Guid transactionId,
        TransactionParticipantReference participant,
        CancellationToken cancellationToken)
    {
        var storage = ResolveParticipantStorage(participant);
        var grainId = TransactionStorageOperations.CreateParticipantRecordGrainId(participant);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = await TransactionStorageOperations.ReadRecordAsync<TransactionalStateParticipantRecord>(
                storage,
                TransactionStorageOperations.ParticipantStateName,
                grainId,
                cancellationToken);
            if (!state.RecordExists
                || state.State.LockedTransactionId != transactionId
                || state.State.PendingWrite is null)
            {
                return;
            }

            state.State.LockedTransactionId = null;
            state.State.PendingWrite = null;

            try
            {
                await storage.WriteStateAsync(
                    TransactionStorageOperations.ParticipantStateName,
                    grainId,
                    state,
                    cancellationToken);
                return;
            }
            catch (Exception exception) when (TransactionStorageOperations.IsWriteConflict(exception))
            {
            }
        }
    }

    private async ValueTask AbortParticipantsAsync(
        IReadOnlyList<TransactionParticipantReference> participants,
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        foreach (var participant in participants)
        {
            await AbortParticipantAsync(transactionId, participant, cancellationToken);
        }
    }

    private async ValueTask<TransactionRecord> ReadTransactionRecordRequiredAsync(
        Guid transactionId,
        CancellationToken cancellationToken)
        => await TryReadTransactionRecordAsync(transactionId, cancellationToken)
            ?? throw new TransactionAbortedException(transactionId, "transaction log record is missing");

    private async ValueTask<TransactionRecord?> TryReadTransactionRecordAsync(
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        var storage = ResolveTransactionLogStorage();
        var state = await TransactionStorageOperations.ReadRecordAsync<TransactionRecord>(
            storage,
            TransactionStorageOperations.TransactionStateName,
            TransactionStorageOperations.CreateTransactionRecordGrainId(transactionId),
            cancellationToken);
        return state.RecordExists
            ? state.State
            : null;
    }

    private async ValueTask MarkStatusAsync(
        Guid transactionId,
        TransactionStatus status,
        CancellationToken cancellationToken)
    {
        var storage = ResolveTransactionLogStorage();
        var grainId = TransactionStorageOperations.CreateTransactionRecordGrainId(transactionId);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = await TransactionStorageOperations.ReadRecordAsync<TransactionRecord>(
                storage,
                TransactionStorageOperations.TransactionStateName,
                grainId,
                cancellationToken);
            if (!state.RecordExists)
            {
                throw new TransactionAbortedException(transactionId, "transaction log record is missing");
            }

            if (state.State.Status == status)
            {
                return;
            }

            if (state.State.Status == TransactionStatus.Committed
                && status == TransactionStatus.Aborted)
            {
                return;
            }

            state.State.Status = status;
            state.State.UpdatedUtc = _timeProvider.GetUtcNow();

            try
            {
                await storage.WriteStateAsync(
                    TransactionStorageOperations.TransactionStateName,
                    grainId,
                    state,
                    cancellationToken);
                return;
            }
            catch (Exception exception) when (TransactionStorageOperations.IsWriteConflict(exception))
            {
            }
        }
    }

    private IGrainStorage ResolveTransactionLogStorage()
        => _storageResolver.Resolve(storageName: null, "transaction log storage");

    private IGrainStorage ResolveParticipantStorage(TransactionParticipantReference participant)
        => _storageResolver.Resolve(participant.StorageName, "transactional state coordination");
}
