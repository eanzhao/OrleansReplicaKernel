using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Routing;

public sealed class InMemoryGrainDirectory : IGrainDirectory
{
    private readonly object _lock = new();
    private readonly IClusterMembershipView _membershipView;
    private readonly IPlacementPolicy _placementPolicy;
    private readonly IPlacementLoadProvider _loadProvider;
    private readonly IOwnerRelocationPolicy _relocationPolicy;
    private readonly Dictionary<GrainId, GrainOwnerRecord> _records = new();

    public InMemoryGrainDirectory(
        IClusterMembershipView membershipView,
        IPlacementPolicy placementPolicy,
        IPlacementLoadProvider loadProvider,
        IOwnerRelocationPolicy relocationPolicy)
        : this(membershipView, placementPolicy, loadProvider, relocationPolicy, checkpoint: null)
    {
    }

    private InMemoryGrainDirectory(
        IClusterMembershipView membershipView,
        IPlacementPolicy placementPolicy,
        IPlacementLoadProvider loadProvider,
        IOwnerRelocationPolicy relocationPolicy,
        GrainDirectoryCheckpoint? checkpoint)
    {
        _membershipView = membershipView;
        _placementPolicy = placementPolicy;
        _loadProvider = loadProvider;
        _relocationPolicy = relocationPolicy;

        if (checkpoint is null)
        {
            return;
        }

        foreach (var record in checkpoint.Records.OrderBy(item => item.GrainId.ToString(), StringComparer.Ordinal))
        {
            _records[record.GrainId] = record;
        }
    }

    public GrainOwnerRecord Resolve(GrainId grainId)
    {
        lock (_lock)
        {
            if (_records.TryGetValue(grainId, out var record))
            {
                if (_membershipView.GetHealth(record.OwnerNodeName) == NodeHealthStatus.Unhealthy)
                {
                    var fallbackOwnerNodeName = _relocationPolicy.SelectOwner(
                        grainId,
                        record.OwnerNodeName,
                        _membershipView);
                    var relocated = record with
                    {
                        OwnerNodeName = fallbackOwnerNodeName,
                        Version = record.Version + 1,
                    };

                    _records[grainId] = relocated;

                    TraceLog.Write(
                        "grain-directory",
                        $"relocate owner {grainId} {record.OwnerNodeName} -> {relocated.OwnerNodeName} v{relocated.Version} epoch={_membershipView.CurrentEpoch}");
                    return relocated;
                }

                TraceLog.Write(
                    "grain-directory",
                    $"resolve owner {grainId} -> {record.OwnerNodeName} v{record.Version}");
                return record;
            }

            var initialOwnerNodeName = _placementPolicy.SelectInitialOwner(
                grainId,
                _membershipView,
                _loadProvider.GetSnapshot());
            var created = new GrainOwnerRecord(grainId, initialOwnerNodeName, Version: 1);
            _records.Add(grainId, created);

            TraceLog.Write(
                "grain-directory",
                $"create placed owner {grainId} -> {created.OwnerNodeName} v{created.Version}");
            return created;
        }
    }

    public GrainOwnerRecord SetOwner(GrainId grainId, string ownerNodeName)
    {
        lock (_lock)
        {
            var current = Resolve(grainId);
            if (current.OwnerNodeName == ownerNodeName)
            {
                TraceLog.Write(
                    "grain-directory",
                    $"owner unchanged {grainId} -> {ownerNodeName} v{current.Version}");
                return current;
            }

            var updated = current with
            {
                OwnerNodeName = ownerNodeName,
                Version = current.Version + 1,
            };

            _records[grainId] = updated;

            TraceLog.Write(
                "grain-directory",
                $"move owner {grainId} -> {updated.OwnerNodeName} v{updated.Version}");
            return updated;
        }
    }

    public GrainDirectoryCheckpoint ExportCheckpoint()
    {
        lock (_lock)
        {
            return new GrainDirectoryCheckpoint(
                _records.Values
                    .OrderBy(item => item.GrainId.ToString(), StringComparer.Ordinal)
                    .ToArray());
        }
    }

    public static InMemoryGrainDirectory Restore(
        IClusterMembershipView membershipView,
        IPlacementPolicy placementPolicy,
        IPlacementLoadProvider loadProvider,
        IOwnerRelocationPolicy relocationPolicy,
        GrainDirectoryCheckpoint checkpoint)
        => new(membershipView, placementPolicy, loadProvider, relocationPolicy, checkpoint);
}
