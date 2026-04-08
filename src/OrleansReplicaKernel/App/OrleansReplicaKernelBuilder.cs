using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Routing;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.App;

public sealed class OrleansReplicaKernelBuilder
{
    private readonly Dictionary<Type, GrainRegistration> _registrations = new();
    private TimeSpan _membershipStabilizationWindow = TimeSpan.FromMilliseconds(200);
    private int _membershipGossipFanout = 1;
    private int _membershipAntiEntropyInterval = 4;
    private OrleansReplicaKernelMembershipCheckpoint? _membershipCheckpoint;
    private OrleansReplicaKernelRuntimeCheckpoint? _runtimeCheckpoint;

    public OrleansReplicaKernelBuilder AddGrain<TContract, TGrain>(
        string grainType,
        Func<TGrain> grainFactory,
        Func<IInvocationRuntime, GrainId, TContract> referenceFactory)
        where TContract : class
        where TGrain : class
    {
        _registrations.Add(
            typeof(TContract),
            new GrainRegistration(
                typeof(TContract),
                grainType,
                () => grainFactory(),
                (runtime, grainId) => referenceFactory(runtime, grainId)));

        return this;
    }

    public OrleansReplicaKernelBuilder WithMembershipStabilizationWindow(TimeSpan stabilizationWindow)
    {
        if (stabilizationWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stabilizationWindow),
                "Membership stabilization window must be non-negative.");
        }

        _membershipStabilizationWindow = stabilizationWindow;
        return this;
    }

    public OrleansReplicaKernelBuilder WithMembershipGossipFanout(int fanout)
    {
        if (fanout <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fanout), "Membership gossip fanout must be positive.");
        }

        _membershipGossipFanout = fanout;
        return this;
    }

    public OrleansReplicaKernelBuilder WithMembershipAntiEntropyInterval(int tickInterval)
    {
        if (tickInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tickInterval),
                "Membership anti-entropy interval must be positive.");
        }

        _membershipAntiEntropyInterval = tickInterval;
        return this;
    }

    public OrleansReplicaKernelBuilder WithMembershipCheckpoint(OrleansReplicaKernelMembershipCheckpoint checkpoint)
    {
        _membershipCheckpoint = checkpoint;
        _runtimeCheckpoint = null;
        return this;
    }

    public OrleansReplicaKernelBuilder WithRuntimeCheckpoint(OrleansReplicaKernelRuntimeCheckpoint checkpoint)
    {
        _runtimeCheckpoint = checkpoint;
        _membershipCheckpoint = checkpoint.Membership;
        return this;
    }

    public OrleansReplicaKernelHost Build(string nodeName)
    {
        return Build(nodeName, []);
    }

    public OrleansReplicaKernelHost Build(string nodeName, params string[] peerNodeNames)
    {
        var requestedNodeNames = new[] { nodeName }
            .Concat(peerNodeNames)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var grainFactories = _registrations.Values.ToDictionary(item => item.GrainType, item => item.GrainFactory);

        InProcessClusterMembership membership;
        string[] allNodeNames;

        var runtimeCheckpoint = _runtimeCheckpoint;

        if (_membershipCheckpoint is null)
        {
            membership = new InProcessClusterMembership();
            allNodeNames = requestedNodeNames;
            foreach (var currentNodeName in allNodeNames)
            {
                membership.Register(currentNodeName);
            }
        }
        else
        {
            membership = InProcessClusterMembership.Restore(_membershipCheckpoint.ClusterMembership);
            allNodeNames = _membershipCheckpoint.ClusterMembership.Members
                .Select(item => item.NodeName)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();

            var requested = requestedNodeNames.OrderBy(item => item, StringComparer.Ordinal).ToArray();
            if (!requested.SequenceEqual(allNodeNames, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Requested node set '{string.Join(", ", requested)}' does not match membership checkpoint nodes '{string.Join(", ", allNodeNames)}'.");
            }
        }

        var failureDetector = new ConsecutiveFailureDetector(membership);
        var nodeRegistry = new InProcessNodeRegistry();

        var membershipViews = _membershipCheckpoint is null
            ? allNodeNames.ToDictionary(
                currentNodeName => currentNodeName,
                currentNodeName => new GossipedClusterMembershipView(
                    currentNodeName,
                    _membershipStabilizationWindow),
                StringComparer.Ordinal)
            : allNodeNames.ToDictionary(
                currentNodeName => currentNodeName,
                currentNodeName =>
                {
                    var viewCheckpoint = _membershipCheckpoint.Views.FirstOrDefault(item =>
                        string.Equals(item.ObserverNodeName, currentNodeName, StringComparison.Ordinal));
                    if (viewCheckpoint is null)
                    {
                        throw new InvalidOperationException(
                            $"Membership checkpoint does not contain observer view '{currentNodeName}'.");
                    }

                    return GossipedClusterMembershipView.Restore(
                        viewCheckpoint,
                        _membershipStabilizationWindow);
                },
                StringComparer.Ordinal);

        var membershipGossiper = new InProcessMembershipGossiper(
            membership,
            membershipViews,
            _membershipGossipFanout,
            _membershipAntiEntropyInterval,
            _membershipCheckpoint?.Dissemination);

        if (_membershipCheckpoint is null)
        {
            membershipGossiper.Gossip();
        }

        var activationDirectories = new Dictionary<string, IActivationDirectory>(StringComparer.Ordinal);
        var loadProvider = new ActivationDirectoryLoadProvider(activationDirectories);
        var placementPolicy = new LeastLoadedPlacementPolicy(nodeName);
        var relocationPolicy = new HealthyNodeRelocationPolicy(nodeName);
        var rebalancingPolicy = new LoadSkewRebalancingPolicy(minimumSkew: 1);
        var grainDirectory = runtimeCheckpoint is null
            ? new InMemoryGrainDirectory(
                membershipViews[nodeName],
                placementPolicy,
                loadProvider,
                relocationPolicy)
            : InMemoryGrainDirectory.Restore(
                membershipViews[nodeName],
                placementPolicy,
                loadProvider,
                relocationPolicy,
                runtimeCheckpoint.GrainDirectory);
        var probeService = new InProcessClusterProbeService(nodeName, membership, nodeRegistry, failureDetector);
        var locators = new Dictionary<string, IGrainLocator>(StringComparer.Ordinal);
        var runtimes = new Dictionary<string, InProcessRuntime>(StringComparer.Ordinal);

        foreach (var currentNodeName in allNodeNames)
        {
            var transport = new InProcessMessageTransport(membershipViews[currentNodeName], nodeRegistry);
            var locator = new DirectoryGrainLocator(grainDirectory);
            var activationCheckpoint = runtimeCheckpoint?.ActivationDirectories
                .FirstOrDefault(item => string.Equals(item.NodeName, currentNodeName, StringComparison.Ordinal));
            var activationDirectory = activationCheckpoint is null
                ? new LocalActivationDirectory(grainFactories)
                : LocalActivationDirectory.Restore(grainFactories, activationCheckpoint);
            var router = new LocalGrainRouter(currentNodeName, locator);
            var runtime = new InProcessRuntime(currentNodeName, failureDetector, locator, router, activationDirectory, transport);

            locators.Add(currentNodeName, locator);
            activationDirectories.Add(currentNodeName, activationDirectory);
            runtimes.Add(currentNodeName, runtime);
            nodeRegistry.Register(currentNodeName, runtime);
        }

        var bindings = _registrations.ToDictionary(
            item => item.Key,
            item => new OrleansReplicaKernelRegistration(item.Value.GrainType, item.Value.ReferenceFactory));

        return new OrleansReplicaKernelHost(
            nodeName,
            runtimes[nodeName],
            grainDirectory,
            membership,
            failureDetector,
            loadProvider,
            rebalancingPolicy,
            nodeRegistry,
            probeService,
            membershipGossiper,
            membershipViews,
            locators,
            activationDirectories,
            runtimes.Values.Cast<IAsyncDisposable>().ToArray(),
            bindings);
    }

    private sealed record GrainRegistration(
        Type ContractType,
        string GrainType,
        Func<object> GrainFactory,
        Func<IInvocationRuntime, GrainId, object> ReferenceFactory);
}
