using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Storage;

internal sealed class GrainActivationContext
{
    private readonly PersistentStateFactory _persistentStateFactory;
    private readonly Dictionary<PersistentStateCacheKey, IPersistentStateParticipant> _persistentStates = new();

    public GrainActivationContext(GrainId grainId, PersistentStateFactory persistentStateFactory)
    {
        GrainId = grainId;
        _persistentStateFactory = persistentStateFactory ?? throw new ArgumentNullException(nameof(persistentStateFactory));
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
}
