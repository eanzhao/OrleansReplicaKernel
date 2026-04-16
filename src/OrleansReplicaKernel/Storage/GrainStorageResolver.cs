namespace OrleansReplicaKernel.Storage;

internal sealed class GrainStorageResolver
{
    public static GrainStorageResolver Empty { get; } = new(
        defaultStorage: null,
        new Dictionary<string, IGrainStorage>(StringComparer.Ordinal));

    private readonly IGrainStorage? _defaultStorage;
    private readonly IReadOnlyDictionary<string, IGrainStorage> _namedStorages;

    public GrainStorageResolver(
        IGrainStorage? defaultStorage,
        IReadOnlyDictionary<string, IGrainStorage> namedStorages)
    {
        _defaultStorage = defaultStorage;
        _namedStorages = new Dictionary<string, IGrainStorage>(
            namedStorages ?? throw new ArgumentNullException(nameof(namedStorages)),
            StringComparer.Ordinal);
    }

    public bool HasDefaultStorage => _defaultStorage is not null;

    public IGrainStorage Resolve(string? storageName, string usage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(usage);

        if (string.IsNullOrWhiteSpace(storageName))
        {
            return _defaultStorage
                ?? throw new InvalidOperationException(
                    $"No default grain storage provider is configured for {usage}.");
        }

        if (_namedStorages.TryGetValue(storageName, out var storage))
        {
            return storage;
        }

        throw new InvalidOperationException(
            $"No grain storage provider named '{storageName}' is configured for {usage}.");
    }
}
