using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Storage;

internal sealed class PersistentStateFactory
{
    public static PersistentStateFactory Empty { get; } = new(GrainStorageResolver.Empty);

    private readonly GrainStorageResolver _storageResolver;

    public PersistentStateFactory(GrainStorageResolver storageResolver)
    {
        _storageResolver = storageResolver ?? throw new ArgumentNullException(nameof(storageResolver));
    }

    public IPersistentState<TState> Create<TState>(
        GrainId grainId,
        string stateName,
        string? storageName)
        => new PersistentState<TState>(
            stateName,
            grainId,
            _storageResolver.Resolve(storageName, "persistent state injection"),
            storageName);
}
