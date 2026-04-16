using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Storage;

namespace OrleansReplicaKernel.Transactions;

public sealed class TransactionalState<TState> : ITransactionalState<TState>
{
    private readonly GrainId _grainId;
    private readonly string _stateName;
    private readonly string? _storageName;
    private readonly IGrainStorage _grainStorage;
    private readonly TransactionCoordinator _transactionCoordinator;
    private readonly TimeProvider _timeProvider;
    private readonly TransactionParticipantReference _participant;

    internal TransactionalState(
        GrainId grainId,
        string stateName,
        string? storageName,
        IGrainStorage grainStorage,
        TransactionCoordinator transactionCoordinator,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateName);

        _grainId = grainId;
        _stateName = stateName;
        _storageName = string.IsNullOrWhiteSpace(storageName) ? null : storageName;
        _grainStorage = grainStorage ?? throw new ArgumentNullException(nameof(grainStorage));
        _transactionCoordinator = transactionCoordinator ?? throw new ArgumentNullException(nameof(transactionCoordinator));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _participant = new TransactionParticipantReference(grainId, stateName, _storageName);
    }

    public async ValueTask<TResult> PerformReadAsync<TResult>(
        Func<TState, TResult> read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);

        await _transactionCoordinator.ResolveParticipantAsync(_participant, cancellationToken);

        var currentTransaction = TransactionContext.Current;
        if (currentTransaction is not null)
        {
            await _transactionCoordinator.RegisterParticipantAsync(
                currentTransaction.TransactionId, _participant, cancellationToken);
        }

        var record = await ReadParticipantRecordAsync(cancellationToken);
        if (currentTransaction is not null
            && record.LockedTransactionId == currentTransaction.TransactionId
            && record.PendingWrite is not null)
        {
            return read(TransactionStateSerializer.Deserialize<TState>(record.PendingWrite.State));
        }

        return read(TransactionStateSerializer.Deserialize<TState>(record.CommittedState));
    }

    public async ValueTask<TResult> PerformUpdateAsync<TResult>(
        Func<TState, TResult> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var transaction = TransactionContext.Current
            ?? throw new InvalidOperationException(
                $"Transactional state '{_stateName}' on '{_grainId}' requires an ambient transaction. Use RunTransactionAsync(...) to perform updates.");

        await _transactionCoordinator.RegisterParticipantAsync(transaction.TransactionId, _participant, cancellationToken);
        await _transactionCoordinator.ResolveParticipantAsync(_participant, cancellationToken);

        var participantRecordId = TransactionStorageOperations.CreateParticipantRecordGrainId(_participant);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = await TransactionStorageOperations.ReadRecordAsync<TransactionalStateParticipantRecord>(
                _grainStorage,
                TransactionStorageOperations.ParticipantStateName,
                participantRecordId,
                cancellationToken);

            var record = state.RecordExists
                ? state.State
                : new TransactionalStateParticipantRecord();

            if (record.LockedTransactionId is { } lockedTransactionId
                && lockedTransactionId != transaction.TransactionId)
            {
                throw new TransactionConflictException(
                    transaction.TransactionId,
                    _grainId,
                    _stateName,
                    lockedTransactionId);
            }

            if (record.PendingWrite?.TransactionId == transaction.TransactionId
                && record.PendingWrite.Status == TransactionParticipantWriteStatus.Prepared)
            {
                throw new InvalidOperationException(
                    $"Transactional state '{_stateName}' on '{_grainId}' cannot be mutated after prepare.");
            }

            var workingState = record.PendingWrite?.TransactionId == transaction.TransactionId
                ? TransactionStateSerializer.Deserialize<TState>(record.PendingWrite.State)
                : TransactionStateSerializer.Deserialize<TState>(record.CommittedState);
            workingState = TransactionStateSerializer.Clone(workingState);

            var result = update(workingState);

            record.LockedTransactionId = transaction.TransactionId;
            record.PendingWrite = new TransactionParticipantWrite
            {
                TransactionId = transaction.TransactionId,
                State = TransactionStateSerializer.Serialize(workingState),
                BaseVersion = record.CommittedVersion,
                Status = TransactionParticipantWriteStatus.Active,
                UpdatedUtc = _timeProvider.GetUtcNow()
            };

            state.State = record;

            try
            {
                await _grainStorage.WriteStateAsync(
                    TransactionStorageOperations.ParticipantStateName,
                    participantRecordId,
                    state,
                    cancellationToken);
                TraceLog.Write(
                    "transaction",
                    $"stage tx={transaction.TransactionId:N} grain={_grainId} state={_stateName} version={record.CommittedVersion}");
                return result;
            }
            catch (Exception exception) when (TransactionStorageOperations.IsWriteConflict(exception))
            {
            }
        }
    }

    public async ValueTask PerformUpdateAsync(
        Action<TState> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await PerformUpdateAsync(
            state =>
            {
                update(state);
                return true;
            },
            cancellationToken);
    }

    private async ValueTask<TransactionalStateParticipantRecord> ReadParticipantRecordAsync(
        CancellationToken cancellationToken)
    {
        var state = await TransactionStorageOperations.ReadRecordAsync<TransactionalStateParticipantRecord>(
            _grainStorage,
            TransactionStorageOperations.ParticipantStateName,
            TransactionStorageOperations.CreateParticipantRecordGrainId(_participant),
            cancellationToken);
        return state.RecordExists
            ? state.State
            : new TransactionalStateParticipantRecord();
    }
}
