using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Storage;

namespace OrleansReplicaKernel.Transactions;

internal sealed class TransactionalStateFactory
{
    public static TransactionalStateFactory Empty { get; } = new(
        GrainStorageResolver.Empty,
        TransactionCoordinator.Disabled,
        TimeProvider.System);

    private readonly GrainStorageResolver _storageResolver;
    private readonly TransactionCoordinator _transactionCoordinator;
    private readonly TimeProvider _timeProvider;

    public TransactionalStateFactory(
        GrainStorageResolver storageResolver,
        TransactionCoordinator transactionCoordinator,
        TimeProvider timeProvider)
    {
        _storageResolver = storageResolver ?? throw new ArgumentNullException(nameof(storageResolver));
        _transactionCoordinator = transactionCoordinator ?? throw new ArgumentNullException(nameof(transactionCoordinator));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public ITransactionalState<TState> Create<TState>(
        GrainId grainId,
        string stateName,
        string? storageName)
        => new TransactionalState<TState>(
            grainId,
            stateName,
            storageName,
            _storageResolver.Resolve(storageName, "transactional state injection"),
            _transactionCoordinator,
            _timeProvider);
}
