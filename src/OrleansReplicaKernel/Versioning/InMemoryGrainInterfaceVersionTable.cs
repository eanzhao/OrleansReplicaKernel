namespace OrleansReplicaKernel.Versioning;

public sealed class InMemoryGrainInterfaceVersionTable : IGrainInterfaceVersionTable
{
    private readonly object _lock = new();
    private long _version;
    private GrainInterfaceVersionManifestCheckpoint _checkpoint = GrainInterfaceVersionManifestCheckpoint.Empty;

    public InMemoryGrainInterfaceVersionTable()
    {
    }

    public InMemoryGrainInterfaceVersionTable(GrainInterfaceVersionManifestCheckpoint checkpoint)
    {
        _checkpoint = Clone(checkpoint);
    }

    public ValueTask<GrainInterfaceVersionTableSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(new GrainInterfaceVersionTableSnapshot(_version, Clone(_checkpoint)));
        }
    }

    public ValueTask<bool> WriteAsync(
        GrainInterfaceVersionTableWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (request.ExpectedVersion != _version)
            {
                return ValueTask.FromResult(false);
            }

            _version++;
            _checkpoint = Clone(request.Checkpoint);
            return ValueTask.FromResult(true);
        }
    }

    internal static GrainInterfaceVersionManifestCheckpoint Clone(GrainInterfaceVersionManifestCheckpoint checkpoint)
        => new(
            checkpoint.Nodes
                .OrderBy(item => item.NodeName, StringComparer.Ordinal)
                .Select(item => new NodeGrainInterfaceVersionManifest(
                    item.NodeName,
                    item.SupportedInterfaces
                        .OrderBy(descriptor => descriptor.GrainType, StringComparer.Ordinal)
                        .ThenBy(descriptor => descriptor.CompatibilityFamily, StringComparer.Ordinal)
                        .ThenBy(descriptor => descriptor.Version)
                        .ToArray()))
                .ToArray());
}
