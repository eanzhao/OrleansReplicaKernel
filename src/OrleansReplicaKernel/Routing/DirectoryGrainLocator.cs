using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Routing;

public sealed class DirectoryGrainLocator : IGrainLocator
{
    private readonly object _lock = new();
    private readonly IGrainDirectory _grainDirectory;
    private readonly Dictionary<GrainId, GrainOwnerRecord> _cache = new();

    public DirectoryGrainLocator(IGrainDirectory grainDirectory)
    {
        _grainDirectory = grainDirectory;
    }

    public GrainAddress Locate(GrainId grainId)
    {
        GrainOwnerRecord record;

        lock (_lock)
        {
            if (_cache.TryGetValue(grainId, out record))
            {
                TraceLog.Write(
                    "locator",
                    $"cache hit {grainId} -> {record.OwnerNodeName} v{record.Version}");
                return new GrainAddress(record.OwnerNodeName, grainId, record.Version);
            }
        }

        record = _grainDirectory.Resolve(grainId);

        lock (_lock)
        {
            _cache[grainId] = record;
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
}
