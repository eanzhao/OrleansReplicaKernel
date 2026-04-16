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
    private long _observedInvalidationVersion = -1;

    public DirectoryGrainLocator(
        IGrainDirectory grainDirectory,
        ClusterGrainInterfaceVersionManifest? grainInterfaceVersions = null)
    {
        _grainDirectory = grainDirectory ?? throw new ArgumentNullException(nameof(grainDirectory));
        _grainInterfaceVersions = grainInterfaceVersions;
    }

    public GrainAddress Locate(GrainId grainId, GrainInterfaceVersionDescriptor? requestedInterface = null)
    {
        GrainOwnerRecord record;
        long observedInvalidationVersion;
        var hasCachedRecord = false;

        lock (_lock)
        {
            hasCachedRecord = _cache.TryGetValue(grainId, out record);
            observedInvalidationVersion = _observedInvalidationVersion;
        }

        if (hasCachedRecord)
        {
            var currentInvalidationVersion = _grainDirectory.GetInvalidationVersion();
            if (currentInvalidationVersion == observedInvalidationVersion
                && IsCompatible(record.OwnerNodeName, requestedInterface))
            {
                TraceLog.Write(
                    "locator",
                    $"cache hit {grainId} -> {record.OwnerNodeName} v{record.Version}");
                return new GrainAddress(record.OwnerNodeName, grainId, record.Version);
            }

            var refreshedRecord = _grainDirectory.Resolve(grainId, requestedInterface);
            var refreshedInvalidationVersion = _grainDirectory.GetInvalidationVersion();

            lock (_lock)
            {
                _cache[grainId] = refreshedRecord;
                _observedInvalidationVersion = refreshedInvalidationVersion;
            }

            TraceLog.Write(
                "locator",
                $"refresh cached address for {grainId} -> {refreshedRecord.OwnerNodeName} v{refreshedRecord.Version} after directory version {observedInvalidationVersion} -> {refreshedInvalidationVersion}");
            return new GrainAddress(refreshedRecord.OwnerNodeName, grainId, refreshedRecord.Version);
        }

        record = _grainDirectory.Resolve(grainId, requestedInterface);
        var invalidationVersion = _grainDirectory.GetInvalidationVersion();

        lock (_lock)
        {
            _cache[grainId] = record;
            _observedInvalidationVersion = invalidationVersion;
        }

        TraceLog.Write(
            "locator",
            $"cache miss {grainId} -> {record.OwnerNodeName} v{record.Version}");
        return new GrainAddress(record.OwnerNodeName, grainId, record.Version);
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
