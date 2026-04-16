using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Routing;

public sealed class PersistentGrainDirectory : IGrainDirectory
{
    private readonly IGrainDirectoryTable _grainDirectoryTable;
    private readonly string _localNodeName;
    private readonly IClusterMembershipView _membershipView;
    private readonly IPlacementPolicy _placementPolicy;
    private readonly IPlacementLoadProvider _loadProvider;
    private readonly IOwnerRelocationPolicy _relocationPolicy;
    private readonly IReadOnlyDictionary<string, GrainTypePlacementHint> _placementHints;
    private readonly ConsistentHashDirectoryPartitionResolver _partitionResolver;

    public PersistentGrainDirectory(
        IGrainDirectoryTable grainDirectoryTable,
        IClusterMembershipView membershipView,
        IPlacementPolicy placementPolicy,
        IPlacementLoadProvider loadProvider,
        IOwnerRelocationPolicy relocationPolicy,
        IReadOnlyDictionary<string, GrainTypePlacementHint>? placementHints = null,
        ConsistentHashDirectoryPartitionResolver? partitionResolver = null)
    {
        _grainDirectoryTable = grainDirectoryTable ?? throw new ArgumentNullException(nameof(grainDirectoryTable));
        _membershipView = membershipView ?? throw new ArgumentNullException(nameof(membershipView));
        _placementPolicy = placementPolicy ?? throw new ArgumentNullException(nameof(placementPolicy));
        _loadProvider = loadProvider ?? throw new ArgumentNullException(nameof(loadProvider));
        _relocationPolicy = relocationPolicy ?? throw new ArgumentNullException(nameof(relocationPolicy));
        _placementHints = placementHints ?? new Dictionary<string, GrainTypePlacementHint>(StringComparer.Ordinal);
        _partitionResolver = partitionResolver ?? new ConsistentHashDirectoryPartitionResolver();
        _localNodeName = membershipView.ObserverNodeName;
    }

    public long GetInvalidationVersion()
        => ReadTableSnapshot().Version;

    public GrainOwnerRecord Resolve(GrainId grainId)
    {
        while (true)
        {
            var directoryOwnerNodeName = ResolveDirectoryOwner(grainId);
            var snapshot = ReadTableSnapshot();
            var existing = snapshot.Checkpoint.Records
                .FirstOrDefault(item => item.GrainId == grainId);

            if (!string.IsNullOrWhiteSpace(existing.OwnerNodeName))
            {
                if (_membershipView.GetHealth(existing.OwnerNodeName) == NodeHealthStatus.Unhealthy)
                {
                    var fallbackOwnerNodeName = _relocationPolicy.SelectOwner(
                        grainId,
                        existing.OwnerNodeName,
                        _membershipView);
                    var relocated = existing with
                    {
                        OwnerNodeName = fallbackOwnerNodeName,
                        Version = existing.Version + 1,
                    };

                    var updatedCheckpoint = Upsert(snapshot.Checkpoint, relocated);
                    if (!_grainDirectoryTable.WriteAsync(
                            new GrainDirectoryTableWriteRequest(snapshot.Version, updatedCheckpoint))
                        .GetAwaiter()
                        .GetResult())
                    {
                        continue;
                    }

                    TraceLog.Write(
                        "grain-directory",
                        $"relocate owner {grainId} {existing.OwnerNodeName} -> {relocated.OwnerNodeName} via directory owner {directoryOwnerNodeName} from {_localNodeName} v{relocated.Version} epoch={_membershipView.CurrentEpoch}");
                    return relocated;
                }

                TraceLog.Write(
                    "grain-directory",
                    $"lookup owner {grainId} -> {existing.OwnerNodeName} via directory owner {directoryOwnerNodeName} from {_localNodeName} v{existing.Version}");
                return existing;
            }

            var initialOwnerNodeName = _placementPolicy.SelectInitialOwner(
                grainId,
                _membershipView,
                _loadProvider.GetSnapshot(),
                ResolvePlacementHint(grainId.GrainType));
            var created = new GrainOwnerRecord(grainId, initialOwnerNodeName, Version: 1);
            var createdCheckpoint = Upsert(snapshot.Checkpoint, created);

            if (!_grainDirectoryTable.WriteAsync(
                    new GrainDirectoryTableWriteRequest(snapshot.Version, createdCheckpoint))
                .GetAwaiter()
                .GetResult())
            {
                continue;
            }

            TraceLog.Write(
                "grain-directory",
                $"register owner {grainId} -> {created.OwnerNodeName} via directory owner {directoryOwnerNodeName} from {_localNodeName} v{created.Version}");
            return created;
        }
    }

    public GrainOwnerRecord SetOwner(GrainId grainId, string ownerNodeName)
    {
        while (true)
        {
            var directoryOwnerNodeName = ResolveDirectoryOwner(grainId);
            var snapshot = ReadTableSnapshot();
            var current = snapshot.Checkpoint.Records
                .FirstOrDefault(item => item.GrainId == grainId);

            if (string.IsNullOrWhiteSpace(current.OwnerNodeName))
            {
                current = Resolve(grainId);
                if (current.OwnerNodeName == ownerNodeName)
                {
                    return current;
                }

                continue;
            }

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

            var updatedCheckpoint = Upsert(snapshot.Checkpoint, updated);
            if (!_grainDirectoryTable.WriteAsync(
                    new GrainDirectoryTableWriteRequest(snapshot.Version, updatedCheckpoint))
                .GetAwaiter()
                .GetResult())
            {
                continue;
            }

            TraceLog.Write(
                "grain-directory",
                $"move owner {grainId} -> {updated.OwnerNodeName} via directory owner {directoryOwnerNodeName} from {_localNodeName} v{updated.Version}");
            return updated;
        }
    }

    public GrainDirectoryCheckpoint ExportCheckpoint()
        => GrainDirectoryCheckpointHelper.Clone(ReadTableSnapshot().Checkpoint);

    private GrainDirectoryTableSnapshot ReadTableSnapshot()
        => _grainDirectoryTable.ReadAsync().GetAwaiter().GetResult();

    private static GrainDirectoryCheckpoint Upsert(GrainDirectoryCheckpoint checkpoint, GrainOwnerRecord record)
        => new(
            checkpoint.Records
                .Where(item => item.GrainId != record.GrainId)
                .Append(record)
                .OrderBy(item => item.GrainId.ToString(), StringComparer.Ordinal)
                .ToArray());

    private GrainTypePlacementHint ResolvePlacementHint(string grainType)
        => _placementHints.TryGetValue(grainType, out var hint)
            ? hint
            : GrainTypePlacementHint.Default;

    private string ResolveDirectoryOwner(GrainId grainId)
        => _partitionResolver.SelectOwner(grainId, _membershipView);
}
