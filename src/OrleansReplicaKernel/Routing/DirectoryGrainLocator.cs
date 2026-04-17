using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Versioning;

namespace OrleansReplicaKernel.Routing;

public sealed class DirectoryGrainLocator : IGrainLocator
{
    private readonly object _lock = new();
    private readonly IGrainDirectory _grainDirectory;
    private readonly ClusterGrainInterfaceVersionManifest? _grainInterfaceVersions;
    private readonly Dictionary<GrainId, GrainOwnerRecord> _cache = new();

    public DirectoryGrainLocator(
        IGrainDirectory grainDirectory,
        ClusterGrainInterfaceVersionManifest? grainInterfaceVersions = null)
    {
        _grainDirectory = grainDirectory ?? throw new ArgumentNullException(nameof(grainDirectory));
        _grainInterfaceVersions = grainInterfaceVersions;
    }

    public GrainAddress Locate(GrainId grainId, GrainInterfaceVersionDescriptor? requestedInterface = null)
    {
        GrainOwnerRecord cachedRecord = default;
        var hasCachedRecord = false;

        lock (_lock)
        {
            hasCachedRecord = _cache.TryGetValue(grainId, out cachedRecord);
        }

        if (hasCachedRecord && IsCompatible(cachedRecord.OwnerNodeName, requestedInterface))
        {
            var currentRecordVersion = _grainDirectory.GetRecordVersion(grainId);
            if (currentRecordVersion == cachedRecord.Version)
            {
                TraceLog.Write(
                    "locator",
                    $"cache hit {grainId} -> {cachedRecord.OwnerNodeName} v{cachedRecord.Version}");
                return new GrainAddress(cachedRecord.OwnerNodeName, grainId, cachedRecord.Version);
            }
        }

        var refreshedRecord = _grainDirectory.Resolve(grainId, requestedInterface);

        lock (_lock)
        {
            _cache[grainId] = refreshedRecord;
        }

        if (hasCachedRecord)
        {
            TraceLog.Write(
                "locator",
                $"refresh cached address for {grainId} -> {refreshedRecord.OwnerNodeName} v{refreshedRecord.Version} after cached owner version {cachedRecord.Version}");
            return new GrainAddress(refreshedRecord.OwnerNodeName, grainId, refreshedRecord.Version);
        }

        TraceLog.Write(
            "locator",
            $"cache miss {grainId} -> {refreshedRecord.OwnerNodeName} v{refreshedRecord.Version}");
        return new GrainAddress(refreshedRecord.OwnerNodeName, grainId, refreshedRecord.Version);
    }

    public void Invalidate(GrainId grainId)
    {
        lock (_lock)
        {
            if (_cache.Remove(grainId))
            {
                TraceLog.Write("locator", $"invalidate cached address for {grainId}");
                return;
            }
        }

        TraceLog.Write("locator", $"no cached address to invalidate for {grainId}");
    }

    private bool IsCompatible(string nodeName, GrainInterfaceVersionDescriptor? requestedInterface)
        => requestedInterface is null
            || _grainInterfaceVersions?.Supports(nodeName, requestedInterface) != false;
}
