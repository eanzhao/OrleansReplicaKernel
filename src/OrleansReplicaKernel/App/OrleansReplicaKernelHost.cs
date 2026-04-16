using OrleansReplicaKernel.Diagnostics;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Routing;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Transactions;

namespace OrleansReplicaKernel.App;

public sealed class OrleansReplicaKernelHost : IAsyncDisposable
{
    private readonly string _nodeName;
    private readonly IInvocationRuntime _runtime;
    private readonly IGrainDirectory _grainDirectory;
    private readonly IClusterMembership _membership;
    private readonly IFailureDetector _failureDetector;
    private readonly IPlacementLoadProvider _placementLoadProvider;
    private readonly IProbeReachabilityController? _probeReachabilityController;
    private readonly IRebalancingPolicy _rebalancingPolicy;
    private readonly IClusterProbeService _probeService;
    private readonly ITransportFaultInjector? _transportFaultInjector;
    private readonly IMembershipGossiper _membershipGossiper;
    private readonly IReadOnlyDictionary<string, GossipedClusterMembershipView> _membershipViews;
    private readonly IReadOnlyDictionary<string, IGrainLocator> _locators;
    private readonly IReadOnlyDictionary<string, IActivationDirectory> _activationDirectories;
    private readonly IReadOnlyDictionary<string, LocalCallbackDirectory> _callbackDirectories;
    private readonly ObjectReferenceFactoryRegistry _objectReferenceFactoryRegistry;
    private readonly IReadOnlyDictionary<string, InProcessRuntime> _runtimes;
    private readonly IReadOnlyList<IAsyncDisposable> _managedNodes;
    private readonly IReadOnlyDictionary<Type, OrleansReplicaKernelRegistration> _registrations;
    private readonly TransactionClient _transactionClient;
    private readonly Func<KernelHealthSnapshot> _healthSnapshotProvider;

    internal OrleansReplicaKernelHost(
        string nodeName,
        TimeProvider timeProvider,
        IInvocationRuntime runtime,
        IGrainDirectory grainDirectory,
        IClusterMembership membership,
        IFailureDetector failureDetector,
        IPlacementLoadProvider placementLoadProvider,
        IRebalancingPolicy rebalancingPolicy,
        IProbeReachabilityController? probeReachabilityController,
        IClusterProbeService probeService,
        ITransportFaultInjector? transportFaultInjector,
        IMembershipGossiper membershipGossiper,
        IReadOnlyDictionary<string, GossipedClusterMembershipView> membershipViews,
        IReadOnlyDictionary<string, IGrainLocator> locators,
        IReadOnlyDictionary<string, IActivationDirectory> activationDirectories,
        IReadOnlyDictionary<string, LocalCallbackDirectory> callbackDirectories,
        ObjectReferenceFactoryRegistry objectReferenceFactoryRegistry,
        IReadOnlyDictionary<string, InProcessRuntime> runtimes,
        IReadOnlyList<IAsyncDisposable> managedNodes,
        TransactionClient transactionClient,
        Func<KernelHealthSnapshot> healthSnapshotProvider,
        string? healthCheckUrlPrefix,
        IReadOnlyDictionary<Type, OrleansReplicaKernelRegistration> registrations)
    {
        _nodeName = nodeName;
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _runtime = runtime;
        _grainDirectory = grainDirectory;
        _membership = membership;
        _failureDetector = failureDetector;
        _placementLoadProvider = placementLoadProvider;
        _probeReachabilityController = probeReachabilityController;
        _rebalancingPolicy = rebalancingPolicy;
        _probeService = probeService;
        _transportFaultInjector = transportFaultInjector;
        _membershipGossiper = membershipGossiper;
        _membershipViews = membershipViews;
        _locators = locators;
        _activationDirectories = activationDirectories;
        _callbackDirectories = callbackDirectories;
        _objectReferenceFactoryRegistry = objectReferenceFactoryRegistry;
        _runtimes = runtimes;
        _managedNodes = managedNodes;
        _transactionClient = transactionClient ?? throw new ArgumentNullException(nameof(transactionClient));
        _healthSnapshotProvider = healthSnapshotProvider ?? throw new ArgumentNullException(nameof(healthSnapshotProvider));
        HealthCheckUrlPrefix = healthCheckUrlPrefix;
        _registrations = registrations;
    }

    public TimeProvider TimeProvider { get; }

    public string? HealthCheckUrlPrefix { get; }

    public TContract GetGrain<TContract>(string key)
        where TContract : class
    {
        if (!_registrations.TryGetValue(typeof(TContract), out var registrationObject))
        {
            throw new InvalidOperationException($"No grain contract registered for {typeof(TContract).Name}.");
        }

        var grainId = new GrainId(registrationObject.GrainType, key);

        TraceLog.Write("app", $"get grain {typeof(TContract).Name} -> {grainId}");
        return (TContract)registrationObject.ReferenceFactory(_runtime, grainId);
    }

    public CallbackLease<THandle> RegisterCallbackTarget<THandle>(
        string callbackType,
        object implementation,
        Func<GrainId, THandle> handleFactory)
    {
        if (!_callbackDirectories.TryGetValue(_nodeName, out var callbackDirectory))
        {
            throw new InvalidOperationException($"No callback directory registered for '{_nodeName}'.");
        }

        var callbackGrainId = callbackDirectory.Register(_nodeName, callbackType, implementation);
        var handle = handleFactory(callbackGrainId);

        return new CallbackLease<THandle>(
            callbackGrainId,
            handle,
            grainId => DeleteCallbackTargetAsync(grainId));
    }

    public CallbackLease<TObserver> CreateObserverReference<TObserver>(
        string callbackType,
        TObserver implementation,
        Func<IInvocationRuntime, GrainId, TObserver> referenceFactory)
        where TObserver : class
        => RegisterCallbackTarget(
            callbackType,
            implementation,
            grainId => referenceFactory(_runtime, grainId));

    public CallbackLease<TObjectReference> CreateObjectReference<TObjectReference>(
        string callbackType,
        object implementation)
        where TObjectReference : class
        => RegisterCallbackTarget(
            callbackType,
            implementation,
            grainId => _objectReferenceFactoryRegistry.Create<TObjectReference>(_runtime, grainId));

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        Task.Delay(delay, TimeProvider, cancellationToken);

    public CancellationTokenSource CreateTimeoutSource(TimeSpan delay) => new(delay, TimeProvider);

    public long GetTimestamp() => TimeProvider.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp) => TimeProvider.GetElapsedTime(startingTimestamp);

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        TimeProvider.GetElapsedTime(startingTimestamp, endingTimestamp);

    public KernelHealthSnapshot GetHealthSnapshot() => _healthSnapshotProvider();

    public async Task<T> WaitForAsync<T>(
        Func<ValueTask<T>> probe,
        Func<T, bool> predicate,
        TimeSpan timeout,
        TimeSpan? delayBetweenProbes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(predicate);

        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Wait timeout must be non-negative.");
        }

        if (delayBetweenProbes is { } delay && delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delayBetweenProbes),
                "Delay between probes must be non-negative.");
        }

        var startedAt = GetTimestamp();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var value = await probe();
            if (predicate(value))
            {
                return value;
            }

            if (GetElapsedTime(startedAt) >= timeout)
            {
                throw new TimeoutException("Timed out waiting for the expected host condition.");
            }

            if (delayBetweenProbes is { } probeDelay && probeDelay > TimeSpan.Zero)
            {
                await DelayAsync(probeDelay, cancellationToken);
            }
            else
            {
                await Task.Yield();
            }
        }
    }

    public async ValueTask<bool> DeactivateGrainAsync<TContract>(string key)
        where TContract : class
    {
        var grainId = GetGrainId<TContract>(key);
        var address = _locators[_nodeName].Locate(grainId);

        TraceLog.Write("app", $"deactivate grain {typeof(TContract).Name} -> {grainId}");
        return await DeactivateOnNodeAsync(address);
    }

    public async ValueTask<int> CollectIdleGrainsAsync(TimeSpan idleFor)
    {
        TraceLog.Write("app", $"collect idle grains idle-for={idleFor}");

        var collected = 0;
        foreach (var activationDirectory in _activationDirectories.Values)
        {
            collected += await activationDirectory.CollectIdleAsync(idleFor);
        }

        return collected;
    }

    public async ValueTask SetOwnerAsync<TContract>(string key, string ownerNodeName)
        where TContract : class
    {
        var grainId = GetGrainId<TContract>(key);
        await MoveOwnerAsync(
            grainId,
            ownerNodeName,
            $"set owner {typeof(TContract).Name}");
    }

    public async ValueTask<bool> RebalanceGrainAsync<TContract>(string key)
        where TContract : class
    {
        var grainId = GetGrainId<TContract>(key);
        var currentOwner = _grainDirectory.Resolve(grainId);
        var targetNodeName = _rebalancingPolicy.SelectHandoffTarget(
            currentOwner,
            _membershipViews[_nodeName],
            _placementLoadProvider.GetSnapshot());

        if (targetNodeName is null)
        {
            TraceLog.Write("handoff", $"skip rebalance for {grainId}: no better target");
            return false;
        }

        await MoveOwnerAsync(grainId, targetNodeName, $"handoff {grainId}");
        return true;
    }

    public ValueTask RunTransactionAsync(
        Func<CancellationToken, Task> callback,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return _transactionClient.RunAsync(
            ct => new ValueTask(callback(ct)),
            maxRetries,
            cancellationToken);
    }

    public ValueTask<TResult> RunTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> callback,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return _transactionClient.RunAsync(
            ct => new ValueTask<TResult>(callback(ct)),
            maxRetries,
            cancellationToken);
    }

    public async ValueTask SetNodeHealthAsync(string nodeName, NodeHealthStatus status)
    {
        _membership.SetHealth(nodeName, status, "manual override");

        if (status != NodeHealthStatus.Healthy && _activationDirectories.TryGetValue(nodeName, out var activationDirectory))
        {
            var shedCount = await activationDirectory.DeactivateAllAsync(ActivationDeactivationReason.Shutdown);
            TraceLog.Write("app", $"shed {shedCount} activations on unhealthy node {nodeName}");
        }
    }

    public async ValueTask ReportFailureSignalAsync(string nodeName, string reason)
    {
        _failureDetector.ReportFailure(nodeName, reason);

        if (_membership.GetHealth(nodeName) == NodeHealthStatus.Unhealthy
            && _activationDirectories.TryGetValue(nodeName, out var activationDirectory))
        {
            var shedCount = await activationDirectory.DeactivateAllAsync();
            TraceLog.Write("app", $"shed {shedCount} activations after failure detector marked {nodeName} unhealthy");
        }
    }

    public void ReportSuccessSignal(string nodeName, string reason)
    {
        _failureDetector.ReportSuccess(nodeName, reason);
    }

    public ValueTask SetNodeAvailabilityAsync(string nodeName, bool isAvailable) =>
        SetNodeHealthAsync(nodeName, isAvailable ? NodeHealthStatus.Healthy : NodeHealthStatus.Unhealthy);

    public void SetProbeReachable(string nodeName, bool isReachable)
    {
        if (_probeReachabilityController is null)
        {
            throw new InvalidOperationException("Current host transport does not support probe reachability injection.");
        }

        _probeReachabilityController.SetProbeReachable(nodeName, isReachable);
        TraceLog.Write("app", $"set probe reachability {nodeName} -> {isReachable}");
    }

    public void FailNextProbe(string nodeName, string reason)
    {
        if (_probeReachabilityController is null)
        {
            throw new InvalidOperationException("Current host transport does not support injected probe failures.");
        }

        _probeReachabilityController.FailNextProbe(nodeName, reason);
        TraceLog.Write("app", $"fail next probe on {nodeName}: {reason}");
    }

    public async ValueTask<int> RunProbeTickAsync(CancellationToken cancellationToken = default)
    {
        var probed = await _probeService.ProbePeersAsync(cancellationToken);
        TraceLog.Write("app", $"probe tick complete probes={probed} epoch={_membership.CurrentEpoch}");
        return probed;
    }

    public async ValueTask<IReadOnlyList<MembershipGossipDelivery>> RunGossipTickAsync()
    {
        var deliveries = _membershipGossiper.Gossip();
        var deliveredChanges = deliveries.Sum(item => item.ConsumedChanges);
        var deliveryModes = string.Join(
            ",",
            deliveries
                .Select(item => $"{item.ObserverNodeName}:{item.Mode}[{item.FromExclusiveEpoch}->{item.ToInclusiveEpoch}]@tick{item.TickNumber}")
                .Distinct(StringComparer.Ordinal));

        await ShedUnhealthyActivationsAsync();

        TraceLog.Write(
            "app",
            $"gossip tick complete deliveries={deliveries.Count} changes={deliveredChanges} epoch={_membership.CurrentEpoch} modes=[{deliveryModes}]");
        return deliveries;
    }

    public OrleansReplicaKernelMembershipCheckpoint CaptureMembershipCheckpoint()
    {
        var checkpoint = CaptureRuntimeCheckpoint().Membership;

        TraceLog.Write(
            "app",
            $"capture membership checkpoint epoch={checkpoint.ClusterMembership.CurrentEpoch} views={checkpoint.Views.Count} gossip-tick={checkpoint.Dissemination.TickNumber}");

        return checkpoint;
    }

    public OrleansReplicaKernelRuntimeCheckpoint CaptureRuntimeCheckpoint()
    {
        var membershipCheckpoint = new OrleansReplicaKernelMembershipCheckpoint(
            _membership.ExportCheckpoint(),
            _membershipViews.Values
                .OrderBy(item => item.ObserverNodeName, StringComparer.Ordinal)
                .Select(item => item.ExportCheckpoint())
                .ToArray(),
            _membershipGossiper.ExportCheckpoint());

        var runtimeCheckpoint = new OrleansReplicaKernelRuntimeCheckpoint(
            membershipCheckpoint,
            _grainDirectory.ExportCheckpoint(),
            _activationDirectories
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => item.Value.ExportCheckpoint(item.Key))
                .ToArray());

        TraceLog.Write(
            "app",
            $"capture runtime checkpoint epoch={runtimeCheckpoint.Membership.ClusterMembership.CurrentEpoch} directory-records={runtimeCheckpoint.GrainDirectory.Records.Count} activation-directories={runtimeCheckpoint.ActivationDirectories.Count}");

        return runtimeCheckpoint;
    }

    public string DescribeMembershipView(string observerNodeName)
    {
        return string.Join(
            ", ",
            GetMembershipViewSnapshot(observerNodeName).Select(
                item => $"{item.NodeName}:stable={item.StableStatus}/observed={item.ObservedStatus}@{item.LastObservedEpoch}"));
    }

    public IReadOnlyList<ObservedClusterMemberRecord> GetMembershipViewSnapshot(string observerNodeName)
    {
        if (!_membershipViews.TryGetValue(observerNodeName, out var membershipView))
        {
            throw new InvalidOperationException($"No membership view registered for '{observerNodeName}'.");
        }

        return membershipView.GetMembers();
    }

    public string DescribeGrainDirectory()
    {
        var checkpoint = _grainDirectory.ExportCheckpoint();
        if (checkpoint.Records.Count == 0)
        {
            return "<empty>";
        }

        return string.Join(
            ", ",
            checkpoint.Records.Select(item => $"{item.GrainId}->{item.OwnerNodeName}@v{item.Version}"));
    }

    public string DescribePlacementLoad()
    {
        var snapshot = _placementLoadProvider.GetSnapshot();
        return string.Join(
            ", ",
            snapshot.Nodes.Select(item => $"{item.NodeName}:load={item.ActivationCount}"));
    }

    public string DescribeActivationMetadata(string nodeName)
    {
        if (!_activationDirectories.TryGetValue(nodeName, out var activationDirectory))
        {
            throw new InvalidOperationException($"No activation directory registered for '{nodeName}'.");
        }

        var checkpoint = activationDirectory.ExportCheckpoint(nodeName);
        if (checkpoint.Records.Count == 0)
        {
            return "<empty>";
        }

        return string.Join(
            ", ",
            checkpoint.Records.Select(item =>
                $"{item.GrainId}:{item.InstanceTypeName}@v{item.OwnerVersion}:{item.LastTouchedUtc:O}"));
    }

    public async ValueTask RunClusterMonitoringRoundAsync(CancellationToken cancellationToken = default)
    {
        var probed = await RunProbeTickAsync(cancellationToken);
        var deliveries = await RunGossipTickAsync();
        var deliveredChanges = deliveries.Sum(item => item.ConsumedChanges);

        TraceLog.Write(
            "app",
            $"monitoring round complete probes={probed} gossip-deliveries={deliveries.Count} gossip-changes={deliveredChanges} epoch={_membership.CurrentEpoch}");
    }

    public void DelayNextRequest(string nodeName, TimeSpan delay)
    {
        if (_transportFaultInjector is null)
        {
            throw new InvalidOperationException("Current host transport does not support request delay injection.");
        }

        _transportFaultInjector.DelayNextRequest(nodeName, delay);
        TraceLog.Write("app", $"delay next request on {nodeName} by {delay}");
    }

    public void FailNextRequestWithResponse(string nodeName, string failureMessage)
    {
        if (_transportFaultInjector is null)
        {
            throw new InvalidOperationException("Current host transport does not support injected failure responses.");
        }

        _transportFaultInjector.FailNextRequestWithResponse(nodeName, new InvalidOperationException(failureMessage));
        TraceLog.Write("app", $"inject next response failure on {nodeName}: {failureMessage}");
    }

    public void DropNextResponse(string nodeName, string reason)
    {
        if (_transportFaultInjector is null)
        {
            throw new InvalidOperationException("Current host transport does not support response drop injection.");
        }

        _transportFaultInjector.DropNextResponse(nodeName, reason);
        TraceLog.Write("app", $"drop next response on {nodeName}: {reason}");
    }

    public void DropNextResponseAndReplayLater(string nodeName, TimeSpan delay, string reason)
    {
        if (_transportFaultInjector is null)
        {
            throw new InvalidOperationException("Current host transport does not support response replay injection.");
        }

        _transportFaultInjector.DropNextResponseAndReplayLater(nodeName, delay, reason);
        TraceLog.Write("app", $"drop next response on {nodeName}, then replay after {delay}: {reason}");
    }

    public void DuplicateNextResponse(string nodeName, TimeSpan delay)
    {
        if (_transportFaultInjector is null)
        {
            throw new InvalidOperationException("Current host transport does not support response duplication injection.");
        }

        _transportFaultInjector.DuplicateNextResponse(nodeName, delay);
        TraceLog.Write("app", $"duplicate next response on {nodeName} after {delay}");
    }

    public ResponseDispositionSnapshot GetResponseDispositionSnapshot(string nodeName)
    {
        if (!_runtimes.TryGetValue(nodeName, out var runtime))
        {
            throw new InvalidOperationException($"No runtime registered for '{nodeName}'.");
        }

        return runtime.GetResponseDispositionSnapshot();
    }

    public string DescribeResponseDisposition(string nodeName)
    {
        var snapshot = GetResponseDispositionSnapshot(nodeName);
        return $"accepted={snapshot.AcceptedResponses}, late={snapshot.LateResponses}, stale={snapshot.StaleResponses}, duplicate={snapshot.DuplicateResponses}, pending={snapshot.PendingResponses}, source={snapshot.TrackedSourceRequests}, target={snapshot.CompletedTargetRequests}";
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var managedNode in _managedNodes)
        {
            await managedNode.DisposeAsync();
        }
    }

    private async ValueTask ShedUnhealthyActivationsAsync()
    {
        var primaryMembershipView = _membershipViews[_nodeName];
        foreach (var member in _membership.GetMembers())
        {
            if (primaryMembershipView.GetHealth(member.NodeName) != NodeHealthStatus.Unhealthy
                || !_activationDirectories.TryGetValue(member.NodeName, out var activationDirectory))
            {
                continue;
            }

            var shedCount = await activationDirectory.DeactivateAllAsync();
            if (shedCount > 0)
            {
                TraceLog.Write("app", $"shed {shedCount} activations on unhealthy node {member.NodeName}");
            }
        }
    }

    private GrainId GetGrainId<TContract>(string key)
        where TContract : class
    {
        if (!_registrations.TryGetValue(typeof(TContract), out var registrationObject))
        {
            throw new InvalidOperationException($"No grain contract registered for {typeof(TContract).Name}.");
        }

        return new GrainId(registrationObject.GrainType, key);
    }

    private async ValueTask<bool> DeactivateOnNodeAsync(
        GrainAddress address,
        ActivationDeactivationReason reason = ActivationDeactivationReason.Explicit)
    {
        if (!_activationDirectories.TryGetValue(address.NodeName, out var activationDirectory))
        {
            TraceLog.Write("app", $"no activation directory for node {address.NodeName}");
            return false;
        }

        return await activationDirectory.DeactivateAsync(address, reason);
    }

    private async ValueTask DeleteCallbackTargetAsync(GrainId grainId)
    {
        if (!_callbackDirectories.TryGetValue(_nodeName, out var callbackDirectory))
        {
            throw new InvalidOperationException($"No callback directory registered for '{_nodeName}'.");
        }

        await callbackDirectory.UnregisterAsync(grainId);
    }

    private async ValueTask MoveOwnerAsync(
        GrainId grainId,
        string ownerNodeName,
        string reason)
    {
        var previous = _grainDirectory.Resolve(grainId);
        ActivationHandoffRecord? handoffState = null;

        if (previous.OwnerNodeName != ownerNodeName
            && _activationDirectories.TryGetValue(previous.OwnerNodeName, out var sourceDirectory))
        {
            try
            {
                handoffState = await sourceDirectory.PrepareHandoffAsync(
                    new GrainAddress(previous.OwnerNodeName, grainId, previous.Version));
            }
            catch (Exception exception)
            {
                TraceLog.Write(
                    "handoff-state",
                    $"capture failed for {grainId} on {previous.OwnerNodeName}: {exception.Message}; fallback to cold handoff");
            }
        }

        var updated = _grainDirectory.SetOwner(grainId, ownerNodeName);
        var sourceAddress = new GrainAddress(previous.OwnerNodeName, grainId, updated.Version);
        var targetAddress = new GrainAddress(updated.OwnerNodeName, grainId, updated.Version);

        if (_activationDirectories.TryGetValue(previous.OwnerNodeName, out var fencedSourceDirectory))
        {
            fencedSourceDirectory.Fence(sourceAddress);
        }

        if (_activationDirectories.TryGetValue(updated.OwnerNodeName, out var fencedTargetDirectory))
        {
            fencedTargetDirectory.Fence(targetAddress);
        }

        foreach (var locator in _locators.Values)
        {
            locator.Invalidate(grainId);
        }

        if (handoffState is not null
            && _activationDirectories.TryGetValue(ownerNodeName, out var targetDirectory))
        {
            try
            {
                targetDirectory.StageHandoffState(targetAddress, handoffState);
                TraceLog.Write(
                    "handoff-state",
                    $"{grainId} transfer staged {previous.OwnerNodeName} -> {ownerNodeName} owner-v{updated.Version} captured={handoffState.CapturedUtc:O}");
            }
            catch (Exception exception)
            {
                TraceLog.Write(
                    "handoff-state",
                    $"stage failed for {grainId} on {ownerNodeName}: {exception.Message}; fallback to cold handoff");
            }
        }

        if (previous.OwnerNodeName != updated.OwnerNodeName)
        {
            var deactivated = await DeactivateOnNodeAsync(sourceAddress, ActivationDeactivationReason.Handoff);
            TraceLog.Write(
                "handoff",
                $"{grainId} {previous.OwnerNodeName} -> {updated.OwnerNodeName} reason={reason} shed-old-activation={deactivated}");
        }

        TraceLog.Write(
            "app",
            $"{grainId} owner={updated.OwnerNodeName} v{updated.Version} reason={reason}");
    }
}
