namespace OrleansReplicaKernel.Routing;

public sealed class ActivationDirectoryLoadProvider : IPlacementLoadProvider
{
    private readonly IReadOnlyDictionary<string, IActivationDirectory> _activationDirectories;

    public ActivationDirectoryLoadProvider(IReadOnlyDictionary<string, IActivationDirectory> activationDirectories)
    {
        _activationDirectories = activationDirectories;
    }

    public PlacementLoadSnapshot GetSnapshot()
    {
        return new PlacementLoadSnapshot(
            _activationDirectories
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item =>
                {
                    var checkpoint = item.Value.ExportCheckpoint(item.Key);
                    return new ActivationLoadRecord(item.Key, checkpoint.Records.Count);
                })
                .ToArray());
    }
}
