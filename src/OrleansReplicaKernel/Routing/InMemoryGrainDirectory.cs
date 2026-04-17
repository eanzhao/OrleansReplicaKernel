using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Versioning;

namespace OrleansReplicaKernel.Routing;

public sealed class InMemoryGrainDirectory : IGrainDirectory
{
    private readonly object _lock = new();
    private readonly IClusterMembershipView _membershipView;
    private readonly IPlacementPolicy _placementPolicy;
    private readonly IPlacementLoadProvider _loadProvider;
    private readonly IOwnerRelocationPolicy _relocationPolicy;
    private readonly ClusterGrainInterfaceVersionManifest? _grainInterfaceVersions;
    private readonly IReadOnlyDictionary<string, GrainTypePlacementHint> _placementHints;
    private readonly Dictionary<GrainId, GrainOwnerRecord> _records = new();
    private long _invalidationVersion;

    public InMemoryGrainDirectory(
        IClusterMembershipView membershipView,
        IPlacementPolicy placementPolicy,
        IPlacementLoadProvider loadProvider,
        IOwnerRelocationPolicy relocationPolicy,
        IReadOnlyDictionary<string, GrainTypePlacementHint>? placementHints = null,
        ClusterGrainInterfaceVersionManifest? grainInterfaceVersions = null)
        : this(
            membershipView,
            placementPolicy,
            loadProvider,
            relocationPolicy,
            checkpoint: null,
            placementHints,
            grainInterfaceVersions)
    {
    }

    private InMemoryGrainDirectory(
        IClusterMembershipView membershipView,
        IPlacementPolicy placementPolicy,
        IPlacementLoadProvider loadProvider,
        IOwnerRelocationPolicy relocationPolicy,
        GrainDirectoryCheckpoint? checkpoint,
        IReadOnlyDictionary<string, GrainTypePlacementHint>? placementHints,
        ClusterGrainInterfaceVersionManifest? grainInterfaceVersions)
    {
        _membershipView = membershipView;
        _placementPolicy = placementPolicy;
        _loadProvider = loadProvider;
        _relocationPolicy = relocationPolicy;
        _grainInterfaceVersions = grainInterfaceVersions;
        _placementHints = placementHints ?? new Dictionary<string, GrainTypePlacementHint>(StringComparer.Ordinal);

        if (checkpoint is null)
        {
            return;
        }

        foreach (var record in checkpoint.Records.OrderBy(item => item.GrainId.ToString(), StringComparer.Ordinal))
        {
            _records[record.GrainId] = record;
        }

        _invalidationVersion = checkpoint.Records.Count == 0
            ? 0
            : checkpoint.Records.Max(item => item.Version);
    }

    public long GetInvalidationVersion()
    {
        lock (_lock)
        {
            return _invalidationVersion;
        }
    }

    public long? GetRecordVersion(GrainId grainId)
    {
        lock (_lock)
        {
            return _records.TryGetValue(grainId, out var record)
                ? record.Version
                : null;
        }
    }

    public GrainOwnerRecord Resolve(GrainId grainId, GrainInterfaceVersionDescriptor? requestedInterface = null)
    {
        lock (_lock)
        {
            if (_records.TryGetValue(grainId, out var record))
            {
                if (!IsUsableOwner(record.OwnerNodeName, requestedInterface))
                {
                    var fallbackOwnerNodeName = SelectCompatibleOwner(
                        grainId,
                        record.OwnerNodeName,
                        requestedInterface);
                    var relocated = record with
                    {
                        OwnerNodeName = fallbackOwnerNodeName,
                        Version = record.Version + 1,
                    };

                    _records[grainId] = relocated;
                    _invalidationVersion++;

                    TraceLog.Write(
                        "grain-directory",
                        $"relocate owner {grainId} {record.OwnerNodeName} -> {relocated.OwnerNodeName} v{relocated.Version} epoch={_membershipView.CurrentEpoch} interface={Describe(requestedInterface)}");
                    return relocated;
                }

                TraceLog.Write(
                    "grain-directory",
                    $"resolve owner {grainId} -> {record.OwnerNodeName} v{record.Version}");
                return record;
            }

            var initialOwnerNodeName = SelectCompatibleOwner(grainId, currentOwnerNodeName: null, requestedInterface);
            var created = new GrainOwnerRecord(grainId, initialOwnerNodeName, Version: 1);
            _records.Add(grainId, created);
            _invalidationVersion++;

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
            _invalidationVersion++;

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
        GrainDirectoryCheckpoint checkpoint,
        IReadOnlyDictionary<string, GrainTypePlacementHint>? placementHints = null,
        ClusterGrainInterfaceVersionManifest? grainInterfaceVersions = null)
        => new(
            membershipView,
            placementPolicy,
            loadProvider,
            relocationPolicy,
            checkpoint,
            placementHints,
            grainInterfaceVersions);

    private GrainTypePlacementHint ResolvePlacementHint(string grainType)
        => _placementHints.TryGetValue(grainType, out var hint)
            ? hint
            : GrainTypePlacementHint.Default;

    private bool IsUsableOwner(string ownerNodeName, GrainInterfaceVersionDescriptor? requestedInterface)
        => _membershipView.GetHealth(ownerNodeName) == NodeHealthStatus.Healthy
           && (requestedInterface is null
               || _grainInterfaceVersions?.Supports(ownerNodeName, requestedInterface) != false);

    private string SelectCompatibleOwner(
        GrainId grainId,
        string? currentOwnerNodeName,
        GrainInterfaceVersionDescriptor? requestedInterface)
    {
        var compatibleView = ResolveCompatibleMembershipView(grainId, requestedInterface);
        if (currentOwnerNodeName is not null
            && _membershipView.GetHealth(currentOwnerNodeName) != NodeHealthStatus.Healthy)
        {
            return _relocationPolicy.SelectOwner(grainId, currentOwnerNodeName, compatibleView);
        }

        return _placementPolicy.SelectInitialOwner(
            grainId,
            compatibleView,
            _loadProvider.GetSnapshot(),
            ResolvePlacementHint(grainId.GrainType));
    }

    private IClusterMembershipView ResolveCompatibleMembershipView(
        GrainId grainId,
        GrainInterfaceVersionDescriptor? requestedInterface)
    {
        if (requestedInterface is null)
        {
            return _membershipView;
        }

        var compatibleMembers = _grainInterfaceVersions?.GetCompatibleHealthyMembers(_membershipView, requestedInterface)
            ?? _membershipView.GetHealthyMembers();
        if (compatibleMembers.Count == 0)
        {
            throw new InvalidOperationException(
                $"No compatible placement candidate is available for grain '{grainId}' and interface '{Describe(requestedInterface)}'.");
        }

        return new FilteredClusterMembershipView(_membershipView, compatibleMembers);
    }

    private static string Describe(GrainInterfaceVersionDescriptor? requestedInterface)
        => requestedInterface is null
            ? "<any>"
            : $"{requestedInterface.CompatibilityFamily}@v{requestedInterface.Version}";
}
