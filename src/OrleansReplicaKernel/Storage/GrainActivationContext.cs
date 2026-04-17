using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Transactions;

namespace OrleansReplicaKernel.Storage;

internal sealed class GrainActivationContext : IGrainExtensionContext
{
    private readonly PersistentStateFactory _persistentStateFactory;
    private readonly TransactionalStateFactory _transactionalStateFactory;
    private readonly Dictionary<PersistentStateCacheKey, IPersistentStateParticipant> _persistentStates = new();
    private readonly Dictionary<TransactionalStateCacheKey, object> _transactionalStates = new();

    public GrainActivationContext(
        GrainId grainId,
        PersistentStateFactory persistentStateFactory,
        TransactionalStateFactory transactionalStateFactory)
    {
        GrainId = grainId;
        _persistentStateFactory = persistentStateFactory ?? throw new ArgumentNullException(nameof(persistentStateFactory));
        _transactionalStateFactory = transactionalStateFactory ?? throw new ArgumentNullException(nameof(transactionalStateFactory));
    }

    public GrainId GrainId { get; }

    public IPersistentState<TState> ResolvePersistentState<TState>(string stateName, string? storageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateName);

        var normalizedStorageName = string.IsNullOrWhiteSpace(storageName)
            ? null
            : storageName;
        var key = new PersistentStateCacheKey(typeof(TState), stateName, normalizedStorageName);
        if (_persistentStates.TryGetValue(key, out var existing))
        {
            return (IPersistentState<TState>)existing;
        }

        var created = (IPersistentStateParticipant)_persistentStateFactory.Create<TState>(
            GrainId,
            stateName,
            normalizedStorageName);
        _persistentStates.Add(key, created);
        return (IPersistentState<TState>)created;
    }

    public ITransactionalState<TState> ResolveTransactionalState<TState>(string stateName, string? storageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateName);

        var normalizedStorageName = string.IsNullOrWhiteSpace(storageName)
            ? null
            : storageName;
        var key = new TransactionalStateCacheKey(typeof(TState), stateName, normalizedStorageName);
        if (_transactionalStates.TryGetValue(key, out var existing))
        {
            return (ITransactionalState<TState>)existing;
        }

        var created = _transactionalStateFactory.Create<TState>(
            GrainId,
            stateName,
            normalizedStorageName);
        _transactionalStates.Add(key, created);
        return created;
    }

    public async ValueTask<IPersistentState<TState>> GetPersistentStateAsync<TState>(
        string stateName,
        string? storageName = null,
        CancellationToken cancellationToken = default)
    {
        var state = ResolvePersistentState<TState>(stateName, storageName);
        if (state is IPersistentStateParticipant participant)
        {
            await participant.EnsureInitializedAsync(cancellationToken);
        }

        return state;
    }

    public ValueTask<ITransactionalState<TState>> GetTransactionalStateAsync<TState>(
        string stateName,
        string? storageName = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ResolveTransactionalState<TState>(stateName, storageName));

    public async ValueTask InitializePersistentStatesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var state in _persistentStates.Values)
        {
            await state.EnsureInitializedAsync(cancellationToken);
        }
    }

    private readonly record struct PersistentStateCacheKey(
        Type StateType,
        string StateName,
        string? StorageName);

    private readonly record struct TransactionalStateCacheKey(
        Type StateType,
        string StateName,
        string? StorageName);
}
