using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Storage;

internal sealed class PersistentStateFactory
{
    public static PersistentStateFactory Empty { get; } = new(
        defaultStorage: null,
        new Dictionary<string, IGrainStorage>(StringComparer.Ordinal));

    private readonly IGrainStorage? _defaultStorage;
    private readonly IReadOnlyDictionary<string, IGrainStorage> _namedStorages;

    public PersistentStateFactory(
        IGrainStorage? defaultStorage,
        IReadOnlyDictionary<string, IGrainStorage> namedStorages)
    {
        _defaultStorage = defaultStorage;
        _namedStorages = new Dictionary<string, IGrainStorage>(
            namedStorages ?? throw new ArgumentNullException(nameof(namedStorages)),
            StringComparer.Ordinal);
    }

    public IPersistentState<TState> Create<TState>(
        GrainId grainId,
        string stateName,
        string? storageName)
        => new PersistentState<TState>(
            stateName,
            grainId,
            ResolveStorage(storageName),
            storageName);

    private IGrainStorage ResolveStorage(string? storageName)
    {
        if (string.IsNullOrWhiteSpace(storageName))
        {
            return _defaultStorage
                ?? throw new InvalidOperationException(
                    "No default grain storage provider is configured for persistent state injection.");
        }

        if (_namedStorages.TryGetValue(storageName, out var storage))
        {
            return storage;
        }

        throw new InvalidOperationException(
            $"No grain storage provider named '{storageName}' is configured for persistent state injection.");
    }
}
