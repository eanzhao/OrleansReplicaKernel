using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Diagnostics;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Scheduling;
using OrleansReplicaKernel.Storage;
using OrleansReplicaKernel.Transactions;

namespace OrleansReplicaKernel.Routing;

public sealed class LocalActivationDirectory : IActivationDirectory
{
    private sealed record PendingHandoffState(
        long OwnerVersion,
        ActivationHandoffRecord Record);

    private readonly object _lock = new();
    private readonly string _localNodeName;
    private readonly LocalCallbackDirectory _callbackDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyDictionary<string, Func<GrainActivationContext, object>> _grainFactories;
    private readonly IReadOnlyDictionary<string, GrainTypeCollectionPolicy> _grainCollectionPolicies;
    private readonly IReadOnlyDictionary<string, GrainTypeSchedulingPolicy> _grainSchedulingPolicies;
    private readonly SystemTargetDirectory _systemTargetDirectory;
    private readonly PersistentStateFactory _persistentStateFactory;
    private readonly TransactionalStateFactory _transactionalStateFactory;
    private readonly IReadOnlyDictionary<string, GrainTypePlacementHint> _placementHints;
    private readonly Dictionary<GrainId, ActivationEntry> _activations = new();
    private readonly Dictionary<GrainId, StatelessWorkerPool> _statelessWorkerPools = new();
    private readonly Dictionary<GrainId, PendingHandoffState> _pendingHandoffStates = new();
    private readonly Dictionary<GrainId, ActivationMetadataRecord> _recoveredMetadata = new();
    private readonly Dictionary<GrainId, long> _fencedOwnerVersions = new();

    public LocalActivationDirectory(
        IReadOnlyDictionary<string, Func<object>> grainFactories,
        IReadOnlyDictionary<string, GrainTypeCollectionPolicy> grainCollectionPolicies,
        LocalCallbackDirectory callbackDirectory,
        IReadOnlyDictionary<string, GrainTypeSchedulingPolicy>? grainSchedulingPolicies = null,
        TimeProvider? timeProvider = null,
        SystemTargetDirectory? systemTargetDirectory = null,
        IReadOnlyDictionary<string, GrainTypePlacementHint>? placementHints = null)
        : this(
            "<local>",
            grainFactories,
            grainCollectionPolicies,
            callbackDirectory,
            grainSchedulingPolicies,
            timeProvider,
            systemTargetDirectory,
            placementHints)
    {
    }

    public LocalActivationDirectory(
        string localNodeName,
        IReadOnlyDictionary<string, Func<object>> grainFactories,
        IReadOnlyDictionary<string, GrainTypeCollectionPolicy> grainCollectionPolicies,
        LocalCallbackDirectory callbackDirectory,
        IReadOnlyDictionary<string, GrainTypeSchedulingPolicy>? grainSchedulingPolicies = null,
        TimeProvider? timeProvider = null,
        SystemTargetDirectory? systemTargetDirectory = null,
        IReadOnlyDictionary<string, GrainTypePlacementHint>? placementHints = null)
        : this(
            localNodeName,
            WrapFactories(grainFactories),
            grainCollectionPolicies,
            callbackDirectory,
            PersistentStateFactory.Empty,
            TransactionalStateFactory.Empty,
            grainSchedulingPolicies,
            timeProvider,
            checkpoint: null,
            systemTargetDirectory: systemTargetDirectory,
            placementHints: placementHints)
    {
    }

    internal LocalActivationDirectory(
        string localNodeName,
        IReadOnlyDictionary<string, Func<GrainActivationContext, object>> grainFactories,
        IReadOnlyDictionary<string, GrainTypeCollectionPolicy> grainCollectionPolicies,
        LocalCallbackDirectory callbackDirectory,
        PersistentStateFactory persistentStateFactory,
        TransactionalStateFactory transactionalStateFactory,
        IReadOnlyDictionary<string, GrainTypeSchedulingPolicy>? grainSchedulingPolicies,
        TimeProvider? timeProvider,
        ActivationDirectoryCheckpoint? checkpoint = null,
        SystemTargetDirectory? systemTargetDirectory = null,
        IReadOnlyDictionary<string, GrainTypePlacementHint>? placementHints = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localNodeName);

        _localNodeName = localNodeName;
        _grainFactories = grainFactories;
        _grainCollectionPolicies = grainCollectionPolicies;
        _callbackDirectory = callbackDirectory;
        _persistentStateFactory = persistentStateFactory ?? throw new ArgumentNullException(nameof(persistentStateFactory));
        _transactionalStateFactory = transactionalStateFactory ?? throw new ArgumentNullException(nameof(transactionalStateFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _grainSchedulingPolicies = grainSchedulingPolicies ?? new Dictionary<string, GrainTypeSchedulingPolicy>(StringComparer.Ordinal);
        _placementHints = placementHints ?? new Dictionary<string, GrainTypePlacementHint>(StringComparer.Ordinal);
        _systemTargetDirectory = systemTargetDirectory ?? new SystemTargetDirectory(localNodeName, _timeProvider);

        if (checkpoint is null)
        {
            return;
        }

        foreach (var record in checkpoint.Records.OrderBy(item => item.GrainId.ToString(), StringComparer.Ordinal))
        {
            _recoveredMetadata[record.GrainId] = record;
            _fencedOwnerVersions[record.GrainId] = record.OwnerVersion;
        }
    }

    public int GetActivationCount()
    {
        lock (_lock)
        {
            var statelessWorkerCount = 0;
            foreach (var pool in _statelessWorkerPools.Values)
            {
                statelessWorkerCount += pool.Count;
            }

            return _activations.Count + statelessWorkerCount;
        }
    }

    public ActivationEntry GetOrCreate(GrainAddress address)
    {
        if (SystemTargetId.IsSystemTarget(address.GrainId))
        {
            if (_systemTargetDirectory.TryGet(address.GrainId, out var systemTarget))
            {
                TraceLog.Write("directory", $"resolve system target {address.GrainId} on {address.NodeName}");
                return systemTarget;
            }

            throw new InvalidOperationException(
                $"No system target is registered for '{address.GrainId}' on node '{_localNodeName}'.");
        }

        if (CallbackTargetIdentity.IsCallback(address.GrainId))
        {
            return _callbackDirectory.GetRequired(address.GrainId);
        }

        lock (_lock)
        {
            if (TryRejectStaleRequest(address))
            {
                throw new StaleGrainAddressException(
                    address.GrainId,
                    address.NodeName,
                    address.OwnerVersion,
                    _fencedOwnerVersions[address.GrainId]);
            }

            if (IsStatelessWorker(address.GrainId.GrainType))
            {
                return GetOrCreateStatelessWorker(address);
            }

            if (_activations.TryGetValue(address.GrainId, out var existing))
            {
                if (existing.OwnerVersion != address.OwnerVersion)
                {
                    TraceLog.Write(
                        "fencing",
                        $"reject owner-version mismatch for active {address.GrainId} on {address.NodeName}: request v{address.OwnerVersion}, activation v{existing.OwnerVersion}");
                    throw new StaleGrainAddressException(
                        address.GrainId,
                        address.NodeName,
                        address.OwnerVersion,
                        existing.OwnerVersion);
                }

                TraceLog.Write("directory", $"reuse activation {address.GrainId}");
                return existing;
            }

            if (!_grainFactories.TryGetValue(address.GrainId.GrainType, out var grainFactory))
            {
                throw new InvalidOperationException(
                    $"No activator registered for grain type '{address.GrainId.GrainType}'.");
            }

            if (_recoveredMetadata.Remove(address.GrainId, out var recovered))
            {
                if (recovered.OwnerVersion > address.OwnerVersion)
                {
                    _recoveredMetadata[address.GrainId] = recovered;
                    TraceLog.Write(
                        "fencing",
                        $"reject stale recovered metadata request {address.GrainId} on {address.NodeName}: request v{address.OwnerVersion}, recovered v{recovered.OwnerVersion}");
                    throw new StaleGrainAddressException(
                        address.GrainId,
                        address.NodeName,
                        address.OwnerVersion,
                        recovered.OwnerVersion);
                }

                TraceLog.Write(
                    "directory",
                    $"recover activation metadata {address.GrainId} on {address.NodeName} last-touched={recovered.LastTouchedUtc:O} owner-v{recovered.OwnerVersion}, create fresh instance");
            }

            var activationContext = new GrainActivationContext(
                address.GrainId,
                _persistentStateFactory,
                _transactionalStateFactory);
            var instance = grainFactory(activationContext);

            var created = new ActivationEntry(
                address.GrainId,
                instance,
                address.OwnerVersion,
                ResolveSchedulingPolicy(address.GrainId.GrainType),
                _timeProvider,
                activationContext);
            _fencedOwnerVersions[address.GrainId] = address.OwnerVersion;

            if (_pendingHandoffStates.TryGetValue(address.GrainId, out var pendingHandoff))
            {
                if (pendingHandoff.OwnerVersion == address.OwnerVersion)
                {
                    _pendingHandoffStates.Remove(address.GrainId);
                    TraceLog.Write(
                        "handoff-state",
                        $"apply staged warm handoff state {address.GrainId} on {address.NodeName} v{address.OwnerVersion}");
                    try
                    {
                        created.ApplyHandoffState(pendingHandoff.Record);
                    }
                    catch (Exception exception)
                    {
                        TraceLog.Write(
                            "handoff-state",
                            $"apply failed for {address.GrainId} on {address.NodeName}: {exception.Message}; fallback to cold activation");
                    }
                }
                else if (pendingHandoff.OwnerVersion < address.OwnerVersion)
                {
                    _pendingHandoffStates.Remove(address.GrainId);
                    TraceLog.Write(
                        "handoff-state",
                        $"drop stale staged handoff state {address.GrainId} on {address.NodeName}: staged v{pendingHandoff.OwnerVersion}, request v{address.OwnerVersion}");
                }
                else
                {
                    TraceLog.Write(
                        "fencing",
                        $"reject request behind staged handoff state {address.GrainId} on {address.NodeName}: request v{address.OwnerVersion}, staged v{pendingHandoff.OwnerVersion}");
                    throw new StaleGrainAddressException(
                        address.GrainId,
                        address.NodeName,
                        address.OwnerVersion,
                        pendingHandoff.OwnerVersion);
                }
            }

            _activations.Add(address.GrainId, created);
            OrleansReplicaKernelTelemetry.RecordActivationDelta(1, address.GrainId, _localNodeName);
            TraceLog.Write("directory", $"register activation {address.GrainId} on {address.NodeName}");
            return created;
        }
    }

    public async ValueTask<ActivationHandoffRecord?> PrepareHandoffAsync(GrainAddress address)
    {
        ActivationEntry? activation;
        lock (_lock)
        {
            if (!_activations.TryGetValue(address.GrainId, out activation))
            {
                TraceLog.Write("handoff-state", $"no active activation to capture for {address.GrainId} on {address.NodeName}");
                return null;
            }
        }

        await activation.QuiesceAsync();

        lock (_lock)
        {
            var handoffState = activation.CaptureHandoffState();
            if (handoffState is null)
            {
                TraceLog.Write("handoff-state", $"{address.GrainId} on {address.NodeName} has no warm handoff participant");
                return null;
            }

            TraceLog.Write(
                "handoff-state",
                $"capture staged warm handoff state {address.GrainId} from {address.NodeName}");
            return handoffState;
        }
    }

    public void StageHandoffState(GrainAddress address, ActivationHandoffRecord handoffState)
    {
        lock (_lock)
        {
            _fencedOwnerVersions[address.GrainId] = Math.Max(
                GetFencedOwnerVersion(address.GrainId),
                address.OwnerVersion);

            if (_activations.TryGetValue(address.GrainId, out var existing))
            {
                if (existing.OwnerVersion != address.OwnerVersion)
                {
                    TraceLog.Write(
                        "handoff-state",
                        $"ignore staged handoff state for active {address.GrainId} on {address.NodeName}: active v{existing.OwnerVersion}, staged v{address.OwnerVersion}");
                    return;
                }

                TraceLog.Write(
                    "handoff-state",
                    $"apply warm handoff state immediately to active {address.GrainId} on {address.NodeName}");
                try
                {
                    existing.ApplyHandoffState(handoffState);
                }
                catch (Exception exception)
                {
                    TraceLog.Write(
                        "handoff-state",
                        $"apply-to-active failed for {address.GrainId} on {address.NodeName}: {exception.Message}; keep existing activation state");
                }

                return;
            }

            _pendingHandoffStates[address.GrainId] = new PendingHandoffState(address.OwnerVersion, handoffState);
            TraceLog.Write(
                "handoff-state",
                $"stage warm handoff state {address.GrainId} for {address.NodeName} v{address.OwnerVersion}");
        }
    }

    public void Fence(GrainAddress address)
    {
        lock (_lock)
        {
            var previousFence = GetFencedOwnerVersion(address.GrainId);
            if (address.OwnerVersion <= previousFence)
            {
                return;
            }

            _fencedOwnerVersions[address.GrainId] = address.OwnerVersion;

            if (_pendingHandoffStates.TryGetValue(address.GrainId, out var pending)
                && pending.OwnerVersion < address.OwnerVersion)
            {
                _pendingHandoffStates.Remove(address.GrainId);
            }

            TraceLog.Write(
                "fencing",
                $"fence {address.GrainId} on {address.NodeName} at v{address.OwnerVersion}");
        }
    }

    public async ValueTask<bool> DeactivateAsync(
        GrainAddress address,
        ActivationDeactivationReason reason = ActivationDeactivationReason.Explicit)
    {
        if (SystemTargetId.IsSystemTarget(address.GrainId))
        {
            TraceLog.Write("directory", $"skip deactivate for system target {address.GrainId} on {address.NodeName}");
            return false;
        }

        ActivationEntry? activation;
        lock (_lock)
        {
            if (!_activations.Remove(address.GrainId, out activation))
            {
                TraceLog.Write("directory", $"no activation to deactivate for {address.GrainId}");
                return false;
            }

            TraceLog.Write(
                "directory",
                $"unregister activation {address.GrainId} from {address.NodeName} reason={reason}");
        }

        await activation.QuiesceAsync();
        await activation.DisposeAsync(reason);
        OrleansReplicaKernelTelemetry.RecordActivationDelta(-1, address.GrainId, _localNodeName);
        return true;
    }

    public async ValueTask<int> CollectIdleAsync(TimeSpan idleFor)
    {
        if (idleFor < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleFor), "Idle window must be non-negative.");
        }

        var utcNow = _timeProvider.GetUtcNow();
        List<ActivationEntry> collected = [];

        lock (_lock)
        {
            foreach (var pair in _activations.ToArray())
            {
                var effectiveIdleWindow = ResolveIdleWindow(pair.Key.GrainType, idleFor);
                if (!pair.Value.CanCollect(utcNow, effectiveIdleWindow))
                {
                    continue;
                }

                _activations.Remove(pair.Key);
                collected.Add(pair.Value);
                TraceLog.Write(
                    "directory",
                    $"collect idle activation {pair.Key} idle-window={effectiveIdleWindow}");
            }
        }

        foreach (var activation in collected)
        {
            await activation.QuiesceAsync();
            await activation.DisposeAsync(ActivationDeactivationReason.IdleCollection);
            OrleansReplicaKernelTelemetry.RecordActivationDelta(-1, activation.GrainId, _localNodeName);
        }

        return collected.Count;
    }

    public async ValueTask<int> DeactivateAllAsync(
        ActivationDeactivationReason reason = ActivationDeactivationReason.Shutdown)
    {
        List<ActivationEntry> activations;
        lock (_lock)
        {
            activations = _activations.Values.ToList();
            _activations.Clear();

            foreach (var pool in _statelessWorkerPools.Values)
            {
                activations.AddRange(pool.GetAll());
            }

            _statelessWorkerPools.Clear();
        }

        foreach (var activation in activations)
        {
            await activation.QuiesceAsync();
            await activation.DisposeAsync(reason);
            OrleansReplicaKernelTelemetry.RecordActivationDelta(-1, activation.GrainId, _localNodeName);
        }

        return activations.Count;
    }

    public async ValueTask DisposeAsync()
    {
        await DeactivateAllAsync();
        await _systemTargetDirectory.DisposeAsync();
    }

    public ActivationDirectoryCheckpoint ExportCheckpoint(string nodeName)
    {
        lock (_lock)
        {
            var records = _recoveredMetadata.Values
                .Concat(_activations.Values.Select(item => item.ExportMetadata()))
                .GroupBy(item => item.GrainId)
                .Select(group => group
                    .OrderByDescending(item => item.LastTouchedUtc)
                    .First())
                .OrderBy(item => item.GrainId.ToString(), StringComparer.Ordinal)
                .ToArray();

            return new ActivationDirectoryCheckpoint(nodeName, records);
        }
    }

    public static LocalActivationDirectory Restore(
        IReadOnlyDictionary<string, Func<object>> grainFactories,
        IReadOnlyDictionary<string, GrainTypeCollectionPolicy> grainCollectionPolicies,
        LocalCallbackDirectory callbackDirectory,
        IReadOnlyDictionary<string, GrainTypeSchedulingPolicy>? grainSchedulingPolicies,
        TimeProvider? timeProvider,
        ActivationDirectoryCheckpoint checkpoint,
        SystemTargetDirectory? systemTargetDirectory = null)
        => Restore(
            "<local>",
            grainFactories,
            grainCollectionPolicies,
            callbackDirectory,
            grainSchedulingPolicies,
            timeProvider,
            checkpoint,
            systemTargetDirectory);

    public static LocalActivationDirectory Restore(
        string localNodeName,
        IReadOnlyDictionary<string, Func<object>> grainFactories,
        IReadOnlyDictionary<string, GrainTypeCollectionPolicy> grainCollectionPolicies,
        LocalCallbackDirectory callbackDirectory,
        IReadOnlyDictionary<string, GrainTypeSchedulingPolicy>? grainSchedulingPolicies,
        TimeProvider? timeProvider,
        ActivationDirectoryCheckpoint checkpoint,
        SystemTargetDirectory? systemTargetDirectory = null)
        => new(
            localNodeName,
            WrapFactories(grainFactories),
            grainCollectionPolicies,
            callbackDirectory,
            PersistentStateFactory.Empty,
            TransactionalStateFactory.Empty,
            grainSchedulingPolicies,
            timeProvider,
            checkpoint,
            systemTargetDirectory);

    internal static LocalActivationDirectory Restore(
        string localNodeName,
        IReadOnlyDictionary<string, Func<GrainActivationContext, object>> grainFactories,
        IReadOnlyDictionary<string, GrainTypeCollectionPolicy> grainCollectionPolicies,
        LocalCallbackDirectory callbackDirectory,
        PersistentStateFactory persistentStateFactory,
        TransactionalStateFactory transactionalStateFactory,
        IReadOnlyDictionary<string, GrainTypeSchedulingPolicy>? grainSchedulingPolicies,
        TimeProvider? timeProvider,
        ActivationDirectoryCheckpoint checkpoint,
        SystemTargetDirectory? systemTargetDirectory = null)
        => new(
            localNodeName,
            grainFactories,
            grainCollectionPolicies,
            callbackDirectory,
            persistentStateFactory,
            transactionalStateFactory,
            grainSchedulingPolicies,
            timeProvider,
            checkpoint,
            systemTargetDirectory);

    private TimeSpan ResolveIdleWindow(string grainType, TimeSpan defaultIdleWindow)
    {
        if (_grainCollectionPolicies.TryGetValue(grainType, out var policy))
        {
            return policy.ResolveIdleWindow(defaultIdleWindow);
        }

        return defaultIdleWindow;
    }

    private GrainTypeSchedulingPolicy ResolveSchedulingPolicy(string grainType)
    {
        if (_grainSchedulingPolicies.TryGetValue(grainType, out var policy))
        {
            return policy;
        }

        return GrainTypeSchedulingPolicy.Default;
    }

    private bool TryRejectStaleRequest(GrainAddress address)
    {
        var fencedOwnerVersion = GetFencedOwnerVersion(address.GrainId);
        if (address.OwnerVersion >= fencedOwnerVersion)
        {
            return false;
        }

        TraceLog.Write(
            "fencing",
            $"reject stale request {address.GrainId} on {address.NodeName}: request v{address.OwnerVersion}, fenced v{fencedOwnerVersion}");
        return true;
    }

    private long GetFencedOwnerVersion(GrainId grainId)
        => _fencedOwnerVersions.TryGetValue(grainId, out var version)
            ? version
            : 0;

    private static IReadOnlyDictionary<string, Func<GrainActivationContext, object>> WrapFactories(
        IReadOnlyDictionary<string, Func<object>> grainFactories)
        => grainFactories.ToDictionary(
            item => item.Key,
            item => new Func<GrainActivationContext, object>(_ => item.Value()),
            StringComparer.Ordinal);

    private bool IsStatelessWorker(string grainType)
        => _placementHints.TryGetValue(grainType, out var hint) && hint.IsStatelessWorker;

    private int GetMaxLocalWorkers(string grainType)
        => _placementHints.TryGetValue(grainType, out var hint) && hint.MaxLocalWorkers > 0
            ? hint.MaxLocalWorkers
            : Environment.ProcessorCount;

    private ActivationEntry GetOrCreateStatelessWorker(GrainAddress address)
    {
        if (!_statelessWorkerPools.TryGetValue(address.GrainId, out var pool))
        {
            pool = new StatelessWorkerPool(GetMaxLocalWorkers(address.GrainId.GrainType));
            _statelessWorkerPools[address.GrainId] = pool;
        }

        var selected = pool.SelectOrCreate(() => CreateWorkerActivation(address));
        return selected;
    }

    private ActivationEntry CreateWorkerActivation(GrainAddress address)
    {
        if (!_grainFactories.TryGetValue(address.GrainId.GrainType, out var grainFactory))
        {
            throw new InvalidOperationException(
                $"No activator registered for grain type '{address.GrainId.GrainType}'.");
        }

        var activationContext = new GrainActivationContext(
            address.GrainId,
            _persistentStateFactory,
            _transactionalStateFactory);
        var instance = grainFactory(activationContext);

        var created = new ActivationEntry(
            address.GrainId,
            instance,
            address.OwnerVersion,
            ResolveSchedulingPolicy(address.GrainId.GrainType),
            _timeProvider,
            activationContext);

        _fencedOwnerVersions[address.GrainId] = address.OwnerVersion;
        OrleansReplicaKernelTelemetry.RecordActivationDelta(1, address.GrainId, _localNodeName);
        TraceLog.Write("directory", $"create stateless worker activation {address.GrainId} on {address.NodeName} (pool)");
        return created;
    }

    private static void DisposeFailedActivation(object instance)
    {
        switch (instance)
        {
            case IAsyncDisposable asyncDisposable:
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    internal sealed class StatelessWorkerPool
    {
        private readonly List<ActivationEntry> _workers = new();
        private int _roundRobinIndex;

        public StatelessWorkerPool(int maxWorkers)
        {
            MaxWorkers = maxWorkers;
        }

        public int MaxWorkers { get; }

        public int Count => _workers.Count;

        public ActivationEntry SelectOrCreate(Func<ActivationEntry> factory)
        {
            if (_workers.Count > 0)
            {
                var selected = _workers[_roundRobinIndex % _workers.Count];
                _roundRobinIndex = (_roundRobinIndex + 1) % _workers.Count;

                if (_workers.Count < MaxWorkers)
                {
                    var created = factory();
                    _workers.Add(created);
                    return created;
                }

                return selected;
            }

            var first = factory();
            _workers.Add(first);
            return first;
        }

        public IReadOnlyList<ActivationEntry> GetAll() => _workers;
    }
}
