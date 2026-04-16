using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrleansReplicaKernel.Diagnostics;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Reminders;
using OrleansReplicaKernel.Routing;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Scheduling;
using OrleansReplicaKernel.Security;
using OrleansReplicaKernel.Serialization;
using OrleansReplicaKernel.Storage;
using OrleansReplicaKernel.Streaming;
using OrleansReplicaKernel.Transactions;
using OrleansReplicaKernel.Versioning;

namespace OrleansReplicaKernel.App;

public sealed class OrleansReplicaKernelBuilder
{
    private readonly List<IBinaryCodec> _binaryCodecs = [];
    private readonly HashSet<Assembly> _binaryCodecAssemblies = [];
    private readonly List<GrainOwnerRecord> _seededOwnerRecords = [];
    private readonly Dictionary<string, IPEndPoint> _tcpNodeEndpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, X509Certificate2> _tcpIdentityCertificates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GrainImplementationRegistration> _grainImplementations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IGrainStorage> _namedGrainStorages = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, GrainReferenceRegistration> _grainReferences = new();
    private readonly Dictionary<Type, ObjectReferenceRegistration> _objectReferenceRegistrations = new();
    private readonly HashSet<Assembly> _generatedGrainImplementationAssemblies = [];
    private readonly HashSet<Assembly> _generatedGrainReferenceAssemblies = [];
    private readonly HashSet<Assembly> _generatedObjectReferenceAssemblies = [];
    private readonly Dictionary<string, MemoryStreamProviderConfiguration> _memoryStreamProviders = new(StringComparer.Ordinal);
    private IGrainDirectoryTable? _grainDirectoryTable;
    private IGrainInterfaceVersionTable? _grainInterfaceVersionTable;
    private ILoggerFactory? _loggerFactory;
    private IMembershipTable? _membershipTable;
    private IReminderTable? _reminderTable;
    private TimeSpan _membershipStabilizationWindow = TimeSpan.FromMilliseconds(200);
    private int _membershipGossipFanout = 1;
    private int _membershipAntiEntropyInterval = 4;
    private TimeSpan _reminderScanInterval = TimeSpan.FromMilliseconds(50);
    private TimeSpan _tcpHeartbeatInterval = TimeSpan.FromMilliseconds(250);
    private TimeProvider _timeProvider = TimeProvider.System;
    private bool _useTcpTransport;
    private bool _useTcpTls;
    private TimeSpan _responseHistoryRetention = TimeSpan.FromMinutes(5);
    private OrleansReplicaKernelMembershipCheckpoint? _membershipCheckpoint;
    private OrleansReplicaKernelRuntimeCheckpoint? _runtimeCheckpoint;
    private IGrainStorage? _defaultGrainStorage;
    private string? _healthCheckUrlPrefix;

    public OrleansReplicaKernelBuilder AddGrain<TContract, TGrain>(
        string grainType,
        Func<TGrain> grainFactory,
        Func<IInvocationRuntime, GrainId, TContract> referenceFactory)
        where TContract : class
        where TGrain : class
    {
        AddGrainImplementation(
            grainType,
            _ => grainFactory(),
            collectionAgeLimit: null,
            preferLocalPlacement: false,
            interleavableMethods: null,
            isGenerated: false,
            replaceExisting: false,
            sourceDescription: $"manual grain implementation for '{grainType}'");
        AddGrainReference(
            typeof(TContract),
            grainType,
            (runtime, grainId) => referenceFactory(runtime, grainId),
            isGenerated: false,
            replaceExisting: false,
            sourceDescription: $"manual grain reference registration for '{typeof(TContract).Name}'");

        return this;
    }

    public OrleansReplicaKernelBuilder AddGrainImplementation<TGrain>(
        string grainType,
        Func<TGrain> grainFactory)
        where TGrain : class
    {
        AddGrainImplementation(
            grainType,
            _ => grainFactory(),
            collectionAgeLimit: null,
            preferLocalPlacement: false,
            interleavableMethods: null,
            isGenerated: false,
            replaceExisting: false,
            sourceDescription: $"manual grain implementation for '{grainType}'");

        return this;
    }

    public OrleansReplicaKernelBuilder AddGrainReference<TContract>(
        string grainType,
        Func<IInvocationRuntime, GrainId, TContract> referenceFactory)
        where TContract : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grainType);
        ArgumentNullException.ThrowIfNull(referenceFactory);

        AddGrainReference(
            typeof(TContract),
            grainType,
            (runtime, grainId) => referenceFactory(runtime, grainId),
            isGenerated: false,
            replaceExisting: false,
            sourceDescription: $"manual grain reference registration for '{typeof(TContract).Name}'");

        return this;
    }

    public OrleansReplicaKernelBuilder AddGeneratedGrainImplementationsFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        _generatedGrainImplementationAssemblies.Add(assembly);
        _binaryCodecAssemblies.Add(assembly);
        return this;
    }

    public OrleansReplicaKernelBuilder AddGeneratedGrainReferencesFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        _generatedGrainReferenceAssemblies.Add(assembly);
        _binaryCodecAssemblies.Add(assembly);
        return this;
    }

    public OrleansReplicaKernelBuilder AddObjectReference<TInterface>(
        Func<IInvocationRuntime, GrainId, TInterface> referenceFactory)
        where TInterface : class
    {
        AddObjectReference(
            typeof(TInterface),
            (runtime, grainId) => referenceFactory(runtime, grainId),
            isGenerated: false,
            replaceExisting: false,
            sourceDescription: $"manual registration for '{typeof(TInterface).Name}'");

        return this;
    }

    public OrleansReplicaKernelBuilder AddGeneratedObjectReferencesFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        _generatedObjectReferenceAssemblies.Add(assembly);
        _binaryCodecAssemblies.Add(assembly);
        return this;
    }

    public OrleansReplicaKernelBuilder AddBinaryCodec(IBinaryCodec codec)
    {
        _binaryCodecs.Add(codec ?? throw new ArgumentNullException(nameof(codec)));
        return this;
    }

    public OrleansReplicaKernelBuilder AddBinaryCodecsFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        _binaryCodecAssemblies.Add(assembly);
        return this;
    }

    public OrleansReplicaKernelBuilder WithLoggerFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        return this;
    }

    public OrleansReplicaKernelBuilder UseHealthCheckEndpoint(string urlPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlPrefix);
        _healthCheckUrlPrefix = urlPrefix.EndsWith("/", StringComparison.Ordinal)
            ? urlPrefix
            : urlPrefix + "/";
        return this;
    }

    public OrleansReplicaKernelBuilder UseHealthCheckEndpoint(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return UseHealthCheckEndpoint($"http://{endpoint.Address}:{endpoint.Port}/");
    }

    public OrleansReplicaKernelBuilder SeedGrainOwner(
        string grainType,
        string key,
        string ownerNodeName,
        long version = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grainType);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerNodeName);

        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Seeded grain owner version must be positive.");
        }

        _seededOwnerRecords.Add(new GrainOwnerRecord(new GrainId(grainType, key), ownerNodeName, version));
        return this;
    }

    public OrleansReplicaKernelBuilder UseTcpTransport()
    {
        _useTcpTransport = true;
        return this;
    }

    public OrleansReplicaKernelBuilder UseTcpTls()
    {
        _useTcpTls = true;
        return this;
    }

    public OrleansReplicaKernelBuilder UseMemoryStreamProvider(
        string providerName,
        int maxBatchSize = 32,
        TimeSpan? dispatchInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        if (maxBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBatchSize), "Stream batch size must be positive.");
        }

        var effectiveDispatchInterval = dispatchInterval ?? TimeSpan.FromMilliseconds(20);
        if (effectiveDispatchInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dispatchInterval),
                "Stream dispatch interval must be positive.");
        }

        _memoryStreamProviders[providerName] = new MemoryStreamProviderConfiguration(
            providerName,
            maxBatchSize,
            effectiveDispatchInterval);
        return this;
    }

    public OrleansReplicaKernelBuilder WithTcpHeartbeatInterval(TimeSpan heartbeatInterval)
    {
        if (heartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval), "TCP heartbeat interval must be positive.");
        }

        _tcpHeartbeatInterval = heartbeatInterval;
        return this;
    }

    public OrleansReplicaKernelBuilder WithTcpNodeEndpoint(string nodeName, IPEndPoint endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        ArgumentNullException.ThrowIfNull(endpoint);

        _tcpNodeEndpoints[nodeName] = endpoint;
        return this;
    }

    public OrleansReplicaKernelBuilder WithTcpIdentityCertificate(string nodeName, X509Certificate2 certificate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        ArgumentNullException.ThrowIfNull(certificate);

        _tcpIdentityCertificates[nodeName] = certificate;
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

    public OrleansReplicaKernelBuilder WithTimeProvider(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        return this;
    }

    public OrleansReplicaKernelBuilder WithMembershipTable(IMembershipTable membershipTable)
    {
        _membershipTable = membershipTable ?? throw new ArgumentNullException(nameof(membershipTable));
        return this;
    }

    public OrleansReplicaKernelBuilder UseFileMembershipTable(string path)
    {
        _membershipTable = new FileMembershipTable(path);
        return this;
    }

    public OrleansReplicaKernelBuilder WithGrainDirectoryTable(IGrainDirectoryTable grainDirectoryTable)
    {
        _grainDirectoryTable = grainDirectoryTable ?? throw new ArgumentNullException(nameof(grainDirectoryTable));
        return this;
    }

    public OrleansReplicaKernelBuilder UseFileGrainDirectoryTable(string path)
    {
        _grainDirectoryTable = new FileGrainDirectoryTable(path);
        return this;
    }

    public OrleansReplicaKernelBuilder WithGrainInterfaceVersionTable(IGrainInterfaceVersionTable grainInterfaceVersionTable)
    {
        _grainInterfaceVersionTable = grainInterfaceVersionTable ?? throw new ArgumentNullException(nameof(grainInterfaceVersionTable));
        return this;
    }

    public OrleansReplicaKernelBuilder UseFileGrainInterfaceVersionTable(string path)
    {
        _grainInterfaceVersionTable = new FileGrainInterfaceVersionTable(path);
        return this;
    }

    public OrleansReplicaKernelBuilder WithReminderTable(IReminderTable reminderTable)
    {
        _reminderTable = reminderTable ?? throw new ArgumentNullException(nameof(reminderTable));
        return this;
    }

    public OrleansReplicaKernelBuilder UseFileReminderTable(string path)
    {
        _reminderTable = new FileReminderTable(path);
        return this;
    }

    public OrleansReplicaKernelBuilder WithReminderScanInterval(TimeSpan reminderScanInterval)
    {
        if (reminderScanInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reminderScanInterval),
                "Reminder scan interval must be positive.");
        }

        _reminderScanInterval = reminderScanInterval;
        return this;
    }

    public OrleansReplicaKernelBuilder WithDefaultGrainStorage(IGrainStorage grainStorage)
    {
        _defaultGrainStorage = grainStorage ?? throw new ArgumentNullException(nameof(grainStorage));
        return this;
    }

    public OrleansReplicaKernelBuilder WithGrainStorage(string storageName, IGrainStorage grainStorage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageName);

        _namedGrainStorages[storageName] = grainStorage ?? throw new ArgumentNullException(nameof(grainStorage));
        return this;
    }

    public OrleansReplicaKernelBuilder UseFileGrainStorage(string path)
    {
        _defaultGrainStorage = new FileGrainStorage(path);
        return this;
    }

    public OrleansReplicaKernelBuilder UseFileGrainStorage(string storageName, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageName);

        _namedGrainStorages[storageName] = new FileGrainStorage(path);
        return this;
    }

    public OrleansReplicaKernelBuilder WithResponseHistoryRetention(TimeSpan retention)
    {
        if (retention < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retention),
                "Response history retention must be non-negative.");
        }

        _responseHistoryRetention = retention;
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
        TraceLog.Configure(_loggerFactory);
        RegisterGeneratedGrainImplementations();
        RegisterGeneratedGrainReferences();
        RegisterGeneratedObjectReferences();

        var grainPolicies = BuildGrainPolicies();
        var storageResolver = BuildStorageResolver();
        var transactionCoordinator = new TransactionCoordinator(storageResolver, _timeProvider);
        var persistentStateFactory = BuildPersistentStateFactory(storageResolver);
        var transactionalStateFactory = BuildTransactionalStateFactory(storageResolver, transactionCoordinator);
        var transactionClient = new TransactionClient(transactionCoordinator, _timeProvider);
        var messageSerializer = BuildMessageSerializer();
        var allNodeNames = ResolveNodeNames(nodeName, peerNodeNames);
        var (membership, membershipViews, membershipGossiper) = BuildMembership(nodeName, allNodeNames);
        var grainInterfaceVersions = BuildGrainInterfaceVersions();
        RegisterSupportedInterfaces(
            grainInterfaceVersions,
            _useTcpTransport ? [nodeName] : allNodeNames);
        var failureDetector = new ConsecutiveFailureDetector(membership);
        var objectReferenceFactoryRegistry = BuildObjectReferenceFactoryRegistry();
        if (_useTcpTransport && _memoryStreamProviders.Count > 0)
        {
            throw new InvalidOperationException(
                "The current memory stream provider implementation only supports in-process hosts.");
        }

        if (_useTcpTransport)
        {
            return BuildTcpHost(
                nodeName,
                allNodeNames,
                grainPolicies,
                membership,
                membershipViews,
                grainInterfaceVersions,
                membershipGossiper,
                failureDetector,
                persistentStateFactory,
                transactionalStateFactory,
                transactionClient,
                objectReferenceFactoryRegistry,
                messageSerializer);
        }

        var nodeRegistry = new InProcessNodeRegistry();
        var activationDirectories = new Dictionary<string, IActivationDirectory>(StringComparer.Ordinal);
        var (grainDirectory, loadProvider, rebalancingPolicy) =
            BuildDirectory(
                nodeName,
                membership,
                membershipViews[nodeName],
                grainInterfaceVersions,
                grainPolicies.PlacementHints,
                activationDirectories);
        var probeService = new InProcessClusterProbeService(nodeName, membership, nodeRegistry, failureDetector);
        var (locators, callbackDirectories, runtimes) =
            BuildNodeRuntimes(allNodeNames, grainPolicies, membershipViews, grainInterfaceVersions, nodeRegistry, failureDetector,
                grainDirectory, persistentStateFactory, transactionalStateFactory, objectReferenceFactoryRegistry,
                activationDirectories, messageSerializer);
        var streamProviders = BuildMemoryStreamProviders(messageSerializer.Serializer, runtimes[nodeName]);
        if (streamProviders.Count > 0)
        {
            var streamRuntime = new GrainStreamRuntime(messageSerializer.Serializer, streamProviders);
            foreach (var runtime in runtimes.Values)
            {
                runtime.BindStreamRuntime(streamRuntime);
            }
        }

        var reminderServices = BuildReminderServices(allNodeNames, grainDirectory, runtimes);

        var bindings = BuildBindings();
        var healthSnapshotProvider = CreateHealthSnapshotProvider(nodeName, membership, activationDirectories, runtimes);
        var healthServer = StartHealthServerIfConfigured(healthSnapshotProvider);

        return new OrleansReplicaKernelHost(
            nodeName,
            _timeProvider,
            runtimes[nodeName],
            grainDirectory,
            membership,
            failureDetector,
            loadProvider,
            rebalancingPolicy,
            nodeRegistry,
            probeService,
            nodeRegistry,
            membershipGossiper,
            membershipViews,
            locators,
            activationDirectories,
            callbackDirectories,
            objectReferenceFactoryRegistry,
            runtimes,
            streamProviders.Values.Cast<IAsyncDisposable>()
                .Concat(reminderServices.Values)
                .Concat(runtimes.Values)
                .Concat(callbackDirectories.Values)
                .Concat(healthServer is null ? [] : [healthServer])
                .ToArray(),
            transactionClient,
            healthSnapshotProvider,
            healthServer?.UrlPrefix,
            bindings);
    }

    public OrleansReplicaKernelClient BuildClient(string nodeName, params string[] gatewayNodeNames)
    {
        TraceLog.Configure(_loggerFactory);
        RegisterGeneratedGrainReferences();
        RegisterGeneratedObjectReferences();

        if (!_useTcpTransport)
        {
            throw new InvalidOperationException("Client mode requires TCP transport.");
        }

        if (_memoryStreamProviders.Count > 0)
        {
            throw new InvalidOperationException(
                "The current memory stream provider implementation does not support client mode.");
        }

        var gateways = gatewayNodeNames.Length == 0
            ? _tcpNodeEndpoints.Keys.OrderBy(item => item, StringComparer.Ordinal).ToArray()
            : gatewayNodeNames
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();

        if (gateways.Length == 0)
        {
            throw new InvalidOperationException("Client mode requires at least one configured gateway endpoint.");
        }

        foreach (var gatewayNodeName in gateways)
        {
            if (!_tcpNodeEndpoints.ContainsKey(gatewayNodeName))
            {
                throw new InvalidOperationException($"TCP transport endpoint for gateway '{gatewayNodeName}' is not configured.");
            }
        }

        var storageResolver = BuildStorageResolver();
        var transactionCoordinator = new TransactionCoordinator(storageResolver, _timeProvider);
        var messageSerializer = BuildMessageSerializer();
        var objectReferenceFactoryRegistry = BuildObjectReferenceFactoryRegistry();
        var gatewaySelector = new RoundRobinGatewaySelector(gateways);
        var locator = new GatewayGrainLocator(gatewaySelector);
        var router = new LocalGrainRouter(nodeName, locator);
        var clientSecurity = BuildTcpTransportSecurity(nodeName, InvocationSourceKind.Client);
        var clientIdentity = BuildLocalInvocationIdentity(nodeName, InvocationSourceKind.Client);
        var callbackDirectory = new LocalCallbackDirectory(_timeProvider);
        var activationDirectory = new LocalActivationDirectory(
            nodeName,
            new Dictionary<string, Func<GrainActivationContext, object>>(StringComparer.Ordinal),
            new Dictionary<string, GrainTypeCollectionPolicy>(StringComparer.Ordinal),
            callbackDirectory,
            PersistentStateFactory.Empty,
            TransactionalStateFactory.Empty,
            new Dictionary<string, GrainTypeSchedulingPolicy>(StringComparer.Ordinal),
            _timeProvider);
        var peerEndpoints = gateways.ToDictionary(
            gatewayNodeName => gatewayNodeName,
            gatewayNodeName => _tcpNodeEndpoints[gatewayNodeName],
            StringComparer.Ordinal);
        var transport = new TcpMessageTransport(
            nodeName,
            new IPEndPoint(IPAddress.Loopback, 0),
            peerEndpoints,
            new StaticClusterMembershipView(nodeName, gateways),
            messageSerializer,
            _tcpHeartbeatInterval,
            _timeProvider,
            InvocationSourceKind.Client,
            clientSecurity,
            acceptInboundConnections: false,
            allowUnknownInboundNodes: false);
        var runtime = new InProcessRuntime(
            nodeName,
            new NoOpFailureDetector(),
            locator,
            router,
            activationDirectory,
            transport,
            objectReferenceFactoryRegistry,
            _timeProvider,
            _responseHistoryRetention,
            InvocationSourceKind.Client,
            clientIdentity);

        transport.Bind(runtime, runtime);
        transport.Start();
        transport.PrimeOutboundConnectionsAsync().GetAwaiter().GetResult();

        return new OrleansReplicaKernelClient(
            nodeName,
            _timeProvider,
            runtime,
            callbackDirectory,
            objectReferenceFactoryRegistry,
            gatewaySelector,
            [runtime, callbackDirectory, transport],
            new TransactionClient(transactionCoordinator, _timeProvider),
            BuildBindings());
    }

    private GrainPolicySet BuildGrainPolicies() => new(
        _grainImplementations.Values.ToDictionary(
            item => item.GrainType, item => item.GrainFactory, StringComparer.Ordinal),
        _grainImplementations.Values.ToDictionary(
            item => item.GrainType, item => new GrainTypeCollectionPolicy(item.CollectionAgeLimit), StringComparer.Ordinal),
        _grainImplementations.Values.ToDictionary(
            item => item.GrainType, item => new GrainTypePlacementHint(item.PreferLocalPlacement), StringComparer.Ordinal),
        _grainImplementations.Values.ToDictionary(
            item => item.GrainType, item => new GrainTypeSchedulingPolicy(item.InterleavableMethods), StringComparer.Ordinal));

    private GrainStorageResolver BuildStorageResolver()
        => new(
            _defaultGrainStorage,
            new Dictionary<string, IGrainStorage>(_namedGrainStorages, StringComparer.Ordinal));

    private static PersistentStateFactory BuildPersistentStateFactory(GrainStorageResolver storageResolver)
        => new(storageResolver);

    private TransactionalStateFactory BuildTransactionalStateFactory(
        GrainStorageResolver storageResolver,
        TransactionCoordinator transactionCoordinator)
        => new(storageResolver, transactionCoordinator, _timeProvider);

    private string[] ResolveNodeNames(string nodeName, string[] peerNodeNames)
    {
        var requestedNodeNames = new[] { nodeName }
            .Concat(peerNodeNames)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (_membershipCheckpoint is null)
        {
            return requestedNodeNames;
        }

        var checkpointNodeNames = _membershipCheckpoint.ClusterMembership.Members
            .Select(item => item.NodeName)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

        var requested = requestedNodeNames.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (!requested.SequenceEqual(checkpointNodeNames, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Requested node set '{string.Join(", ", requested)}' does not match membership checkpoint nodes '{string.Join(", ", checkpointNodeNames)}'.");
        }

        return checkpointNodeNames;
    }

    private (IClusterMembership Membership,
        Dictionary<string, GossipedClusterMembershipView> Views,
        InProcessMembershipGossiper Gossiper) BuildMembership(string nodeName, string[] allNodeNames)
    {
        IClusterMembership membership;
        Dictionary<string, GossipedClusterMembershipView> views;

        if (_membershipTable is not null)
        {
            if (_membershipCheckpoint is not null)
            {
                SeedMembershipTableIfEmpty(_membershipTable, _membershipCheckpoint.ClusterMembership);
            }

            membership = new InProcessClusterMembership(_membershipTable, _timeProvider);
            if (!membership.IsMember(nodeName))
            {
                membership.Register(nodeName);
            }

            views = _membershipCheckpoint is null
                ? allNodeNames.ToDictionary(
                    name => name,
                    name => new GossipedClusterMembershipView(name, _membershipStabilizationWindow, _timeProvider),
                    StringComparer.Ordinal)
                : allNodeNames.ToDictionary(
                    name => name,
                    name =>
                    {
                        var viewCheckpoint = _membershipCheckpoint.Views.FirstOrDefault(item =>
                            string.Equals(item.ObserverNodeName, name, StringComparison.Ordinal))
                            ?? throw new InvalidOperationException(
                                $"Membership checkpoint does not contain observer view '{name}'.");
                        return GossipedClusterMembershipView.Restore(
                            viewCheckpoint, _membershipStabilizationWindow, _timeProvider);
                    },
                    StringComparer.Ordinal);
        }
        else if (_membershipCheckpoint is null)
        {
            membership = new InProcessClusterMembership(_timeProvider);
            foreach (var currentNodeName in allNodeNames)
            {
                membership.Register(currentNodeName);
            }

            views = allNodeNames.ToDictionary(
                name => name,
                name => new GossipedClusterMembershipView(name, _membershipStabilizationWindow, _timeProvider),
                StringComparer.Ordinal);
        }
        else
        {
            membership = InProcessClusterMembership.Restore(_membershipCheckpoint.ClusterMembership, _timeProvider);
            views = allNodeNames.ToDictionary(
                name => name,
                name =>
                {
                    var viewCheckpoint = _membershipCheckpoint.Views.FirstOrDefault(item =>
                        string.Equals(item.ObserverNodeName, name, StringComparison.Ordinal))
                        ?? throw new InvalidOperationException(
                            $"Membership checkpoint does not contain observer view '{name}'.");
                    return GossipedClusterMembershipView.Restore(
                        viewCheckpoint, _membershipStabilizationWindow, _timeProvider);
                },
                StringComparer.Ordinal);
        }

        var gossiper = new InProcessMembershipGossiper(
            membership, views, _membershipGossipFanout, _membershipAntiEntropyInterval,
            _membershipCheckpoint?.Dissemination);

        if (_membershipCheckpoint is null)
        {
            gossiper.Gossip();
        }

        return (membership, views, gossiper);
    }

    private static void SeedMembershipTableIfEmpty(
        IMembershipTable membershipTable,
        ClusterMembershipCheckpoint checkpoint)
    {
        var snapshot = membershipTable.ReadAsync().GetAwaiter().GetResult();
        if (!MembershipTableCheckpointHelper.IsEmpty(snapshot.Checkpoint))
        {
            return;
        }

        membershipTable.UpdateAsync(
                new MembershipTableWriteRequest(
                    snapshot.Version,
                    MembershipTableCheckpointHelper.Clone(checkpoint)))
            .GetAwaiter()
            .GetResult();
    }

    private (IGrainDirectory Directory,
        ActivationDirectoryLoadProvider LoadProvider,
        LoadSkewRebalancingPolicy RebalancingPolicy) BuildDirectory(
        string nodeName,
        IClusterMembership membership,
        IClusterMembershipView membershipView,
        ClusterGrainInterfaceVersionManifest grainInterfaceVersions,
        IReadOnlyDictionary<string, GrainTypePlacementHint> placementHints,
        Dictionary<string, IActivationDirectory> activationDirectories)
    {
        var loadProvider = new ActivationDirectoryLoadProvider(activationDirectories);
        var placementPolicy = new LeastLoadedPlacementPolicy(nodeName);
        var relocationPolicy = new HealthyNodeRelocationPolicy(nodeName);
        var rebalancingPolicy = new LoadSkewRebalancingPolicy(minimumSkew: 1);
        var checkpoint = _runtimeCheckpoint?.GrainDirectory ?? BuildSeededDirectoryCheckpoint();

        if (_grainDirectoryTable is not null)
        {
            if (checkpoint is not null)
            {
                SeedGrainDirectoryTableIfEmpty(_grainDirectoryTable, checkpoint);
            }

            var authoritativeMembershipView = new AuthoritativeClusterMembershipView(nodeName, membership, _timeProvider);
            return (
                new PersistentGrainDirectory(
                    _grainDirectoryTable,
                    authoritativeMembershipView,
                    placementPolicy,
                    loadProvider,
                    relocationPolicy,
                    placementHints,
                    grainInterfaceVersions),
                loadProvider,
                rebalancingPolicy);
        }

        var directory = checkpoint is null
            ? new InMemoryGrainDirectory(
                membershipView,
                placementPolicy,
                loadProvider,
                relocationPolicy,
                placementHints,
                grainInterfaceVersions)
            : InMemoryGrainDirectory.Restore(
                membershipView,
                placementPolicy,
                loadProvider,
                relocationPolicy,
                checkpoint,
                placementHints,
                grainInterfaceVersions);

        return (directory, loadProvider, rebalancingPolicy);
    }

    private static void SeedGrainDirectoryTableIfEmpty(
        IGrainDirectoryTable grainDirectoryTable,
        GrainDirectoryCheckpoint checkpoint)
    {
        var snapshot = grainDirectoryTable.ReadAsync().GetAwaiter().GetResult();
        if (!GrainDirectoryCheckpointHelper.IsEmpty(snapshot.Checkpoint))
        {
            return;
        }

        grainDirectoryTable.WriteAsync(
                new GrainDirectoryTableWriteRequest(
                    snapshot.Version,
                    GrainDirectoryCheckpointHelper.Clone(checkpoint)))
            .GetAwaiter()
            .GetResult();
    }

    private ClusterGrainInterfaceVersionManifest BuildGrainInterfaceVersions()
        => new(_grainInterfaceVersionTable ?? new InMemoryGrainInterfaceVersionTable());

    private void RegisterSupportedInterfaces(
        ClusterGrainInterfaceVersionManifest grainInterfaceVersions,
        IEnumerable<string> nodeNames)
    {
        ArgumentNullException.ThrowIfNull(grainInterfaceVersions);
        ArgumentNullException.ThrowIfNull(nodeNames);

        var supportedInterfaces = _grainReferences.Values
            .Select(item => GrainInterfaceVersionMetadata.FromContract(item.ContractType, item.GrainType))
            .Distinct()
            .OrderBy(item => item.GrainType, StringComparer.Ordinal)
            .ThenBy(item => item.CompatibilityFamily, StringComparer.Ordinal)
            .ThenBy(item => item.Version)
            .ToArray();

        foreach (var nodeName in nodeNames.Distinct(StringComparer.Ordinal))
        {
            grainInterfaceVersions.Register(nodeName, supportedInterfaces);
        }
    }

    private ObjectReferenceFactoryRegistry BuildObjectReferenceFactoryRegistry() =>
        new(_objectReferenceRegistrations.ToDictionary(
            item => ObjectReferenceFactoryRegistry.GetInterfaceNameFromType(item.Key),
            item => item.Value.ReferenceFactory,
            StringComparer.Ordinal));

    private BinaryMessageSerializer BuildMessageSerializer()
    {
        var builder = new BinarySerializerBuilder();

        foreach (var codec in _binaryCodecs)
        {
            builder.AddCodec(codec);
        }

        foreach (var assembly in _binaryCodecAssemblies)
        {
            builder.AddCodecsFromAssembly(assembly);
        }

        return new BinaryMessageSerializer(builder.Build());
    }

    private IReadOnlyDictionary<Type, OrleansReplicaKernelRegistration> BuildBindings()
        => _grainReferences.ToDictionary(
            item => item.Key,
            item => new OrleansReplicaKernelRegistration(item.Value.GrainType, item.Value.ReferenceFactory));

    private GrainDirectoryCheckpoint? BuildSeededDirectoryCheckpoint()
    {
        if (_seededOwnerRecords.Count == 0)
        {
            return null;
        }

        return new GrainDirectoryCheckpoint(
            _seededOwnerRecords
                .Distinct()
                .OrderBy(item => item.GrainId.ToString(), StringComparer.Ordinal)
                .ToArray());
    }

    private OrleansReplicaKernelHost BuildTcpHost(
        string nodeName,
        string[] allNodeNames,
        GrainPolicySet grainPolicies,
        IClusterMembership membership,
        Dictionary<string, GossipedClusterMembershipView> membershipViews,
        ClusterGrainInterfaceVersionManifest grainInterfaceVersions,
        InProcessMembershipGossiper membershipGossiper,
        IFailureDetector failureDetector,
        PersistentStateFactory persistentStateFactory,
        TransactionalStateFactory transactionalStateFactory,
        TransactionClient transactionClient,
        ObjectReferenceFactoryRegistry objectReferenceFactoryRegistry,
        BinaryMessageSerializer messageSerializer)
    {
        if (!_tcpNodeEndpoints.TryGetValue(nodeName, out var localEndpoint))
        {
            throw new InvalidOperationException($"TCP transport endpoint for '{nodeName}' is not configured.");
        }

        foreach (var currentNodeName in allNodeNames)
        {
            if (!_tcpNodeEndpoints.ContainsKey(currentNodeName))
            {
                throw new InvalidOperationException($"TCP transport endpoint for '{currentNodeName}' is not configured.");
            }
        }

        var activationDirectories = new Dictionary<string, IActivationDirectory>(StringComparer.Ordinal);
        var (grainDirectory, loadProvider, rebalancingPolicy) =
            BuildDirectory(
                nodeName,
                membership,
                membershipViews[nodeName],
                grainInterfaceVersions,
                grainPolicies.PlacementHints,
                activationDirectories);
        var callbackDirectory = new LocalCallbackDirectory(_timeProvider);
        var activationCheckpoint = _runtimeCheckpoint?.ActivationDirectories
            .FirstOrDefault(item => string.Equals(item.NodeName, nodeName, StringComparison.Ordinal));
        var activationDirectory = activationCheckpoint is null
            ? new LocalActivationDirectory(
                nodeName,
                grainPolicies.Factories,
                grainPolicies.CollectionPolicies,
                callbackDirectory,
                persistentStateFactory,
                transactionalStateFactory,
                grainPolicies.SchedulingPolicies,
                _timeProvider)
            : LocalActivationDirectory.Restore(
                nodeName,
                grainPolicies.Factories,
                grainPolicies.CollectionPolicies,
                callbackDirectory,
                persistentStateFactory,
                transactionalStateFactory,
                grainPolicies.SchedulingPolicies,
                _timeProvider,
                activationCheckpoint);
        activationDirectories.Add(nodeName, activationDirectory);

        var peerEndpoints = _tcpNodeEndpoints
            .Where(item => !string.Equals(item.Key, nodeName, StringComparison.Ordinal))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var transportMembershipView = BuildTransportMembershipView(nodeName, membership, membershipViews[nodeName]);
        var localIdentity = BuildLocalInvocationIdentity(nodeName, InvocationSourceKind.ClusterNode);
        var transport = new TcpMessageTransport(
            nodeName,
            localEndpoint,
            peerEndpoints,
            transportMembershipView,
            messageSerializer,
            _tcpHeartbeatInterval,
            _timeProvider,
            InvocationSourceKind.ClusterNode,
            BuildTcpTransportSecurity(nodeName, InvocationSourceKind.ClusterNode),
            acceptInboundConnections: true,
            allowUnknownInboundNodes: true);
        var locator = new DirectoryGrainLocator(grainDirectory, grainInterfaceVersions);
        var router = new LocalGrainRouter(nodeName, locator);
        var runtime = new InProcessRuntime(
            nodeName,
            failureDetector,
            locator,
            router,
            activationDirectory,
            transport,
            objectReferenceFactoryRegistry,
            _timeProvider,
            _responseHistoryRetention,
            localInvocationIdentity: localIdentity);

        transport.Bind(runtime, runtime);
        LocalReminderService? reminderService = null;
        if (_reminderTable is not null)
        {
            reminderService = new LocalReminderService(
                nodeName,
                _reminderTable,
                grainDirectory,
                _timeProvider,
                _reminderScanInterval);
            reminderService.Bind(runtime);
            runtime.BindReminderService(reminderService);
        }

        transport.Start();

        var bindings = BuildBindings();
        var locators = new Dictionary<string, IGrainLocator>(StringComparer.Ordinal)
        {
            [nodeName] = locator
        };
        var callbackDirectories = new Dictionary<string, LocalCallbackDirectory>(StringComparer.Ordinal)
        {
            [nodeName] = callbackDirectory
        };
        var runtimes = new Dictionary<string, InProcessRuntime>(StringComparer.Ordinal)
        {
            [nodeName] = runtime
        };
        var probeService = new TcpClusterProbeService(nodeName, membership, _tcpNodeEndpoints, failureDetector);
        var healthSnapshotProvider = CreateHealthSnapshotProvider(nodeName, membership, activationDirectories, runtimes);
        var healthServer = StartHealthServerIfConfigured(healthSnapshotProvider);

        return new OrleansReplicaKernelHost(
            nodeName,
            _timeProvider,
            runtime,
            grainDirectory,
            membership,
            failureDetector,
            loadProvider,
            rebalancingPolicy,
            probeReachabilityController: null,
            probeService,
            transportFaultInjector: null,
            membershipGossiper,
            membershipViews,
            locators,
            activationDirectories,
            callbackDirectories,
            objectReferenceFactoryRegistry,
            runtimes,
            reminderService is null
                ? healthServer is null
                    ? [runtime, callbackDirectory, transport]
                    : [runtime, callbackDirectory, transport, healthServer]
                : healthServer is null
                    ? [reminderService, runtime, callbackDirectory, transport]
                    : [reminderService, runtime, callbackDirectory, transport, healthServer],
            transactionClient,
            healthSnapshotProvider,
            healthServer?.UrlPrefix,
            bindings);
    }

    private (Dictionary<string, IGrainLocator> Locators,
        Dictionary<string, LocalCallbackDirectory> CallbackDirectories,
        Dictionary<string, InProcessRuntime> Runtimes) BuildNodeRuntimes(
        string[] allNodeNames,
        GrainPolicySet grainPolicies,
        Dictionary<string, GossipedClusterMembershipView> membershipViews,
        ClusterGrainInterfaceVersionManifest grainInterfaceVersions,
        InProcessNodeRegistry nodeRegistry,
        IFailureDetector failureDetector,
        IGrainDirectory grainDirectory,
        PersistentStateFactory persistentStateFactory,
        TransactionalStateFactory transactionalStateFactory,
        ObjectReferenceFactoryRegistry objectReferenceFactoryRegistry,
        Dictionary<string, IActivationDirectory> activationDirectories,
        BinaryMessageSerializer messageSerializer)
    {
        var locators = new Dictionary<string, IGrainLocator>(StringComparer.Ordinal);
        var callbackDirectories = new Dictionary<string, LocalCallbackDirectory>(StringComparer.Ordinal);
        var runtimes = new Dictionary<string, InProcessRuntime>(StringComparer.Ordinal);

        foreach (var currentNodeName in allNodeNames)
        {
            var transport = new InProcessMessageTransport(
                membershipViews[currentNodeName],
                nodeRegistry,
                messageSerializer,
                _timeProvider);
            var locator = new DirectoryGrainLocator(grainDirectory, grainInterfaceVersions);
            var callbackDirectory = new LocalCallbackDirectory(_timeProvider);
            var activationCheckpoint = _runtimeCheckpoint?.ActivationDirectories
                .FirstOrDefault(item => string.Equals(item.NodeName, currentNodeName, StringComparison.Ordinal));
            var activationDirectory = activationCheckpoint is null
                ? new LocalActivationDirectory(
                    currentNodeName,
                    grainPolicies.Factories, grainPolicies.CollectionPolicies,
                    callbackDirectory, persistentStateFactory, transactionalStateFactory,
                    grainPolicies.SchedulingPolicies, _timeProvider)
                : LocalActivationDirectory.Restore(
                    currentNodeName,
                    grainPolicies.Factories, grainPolicies.CollectionPolicies,
                    callbackDirectory, persistentStateFactory, transactionalStateFactory,
                    grainPolicies.SchedulingPolicies, _timeProvider, activationCheckpoint);
            var router = new LocalGrainRouter(currentNodeName, locator);
            var localIdentity = BuildLocalInvocationIdentity(currentNodeName, InvocationSourceKind.ClusterNode);
            var runtime = new InProcessRuntime(
                currentNodeName, failureDetector, locator, router, activationDirectory,
                transport, objectReferenceFactoryRegistry, _timeProvider, _responseHistoryRetention,
                localInvocationIdentity: localIdentity);

            locators.Add(currentNodeName, locator);
            activationDirectories.Add(currentNodeName, activationDirectory);
            callbackDirectories.Add(currentNodeName, callbackDirectory);
            runtimes.Add(currentNodeName, runtime);
            nodeRegistry.Register(currentNodeName, runtime, runtime);
        }

        return (locators, callbackDirectories, runtimes);
    }

    private IClusterMembershipView BuildTransportMembershipView(
        string nodeName,
        IClusterMembership membership,
        IClusterMembershipView membershipView)
        => _membershipTable is null
            ? membershipView
            : new AuthoritativeClusterMembershipView(nodeName, membership, _timeProvider);

    private InvocationIdentity BuildLocalInvocationIdentity(string nodeName, InvocationSourceKind sourceKind)
    {
        if (_useTcpTransport && _useTcpTls && _tcpIdentityCertificates.TryGetValue(nodeName, out var certificate))
        {
            return InvocationIdentity.CreateAuthenticated(nodeName, sourceKind, certificate);
        }

        return InvocationIdentity.CreateLocal(nodeName, sourceKind);
    }

    private TcpTransportSecurityOptions? BuildTcpTransportSecurity(
        string nodeName,
        InvocationSourceKind localSourceKind)
    {
        if (!_useTcpTls)
        {
            return null;
        }

        if (!_tcpIdentityCertificates.TryGetValue(nodeName, out var localCertificate))
        {
            throw new InvalidOperationException(
                $"TCP TLS is enabled but no identity certificate is registered for '{nodeName}'.");
        }

        var trustedPeers = _tcpIdentityCertificates
            .Where(item => !string.Equals(item.Key, nodeName, StringComparison.Ordinal))
            .Select(item => TcpTransportSecurityOptions.TrustedPeer.Create(
                item.Key,
                ResolveIdentitySourceKind(item.Key, localSourceKind),
                item.Value))
            .ToArray();

        return new TcpTransportSecurityOptions(localCertificate, trustedPeers);
    }

    private InvocationSourceKind ResolveIdentitySourceKind(
        string nodeName,
        InvocationSourceKind localSourceKind)
        => _tcpNodeEndpoints.ContainsKey(nodeName)
            ? InvocationSourceKind.ClusterNode
            : localSourceKind == InvocationSourceKind.Client
                ? InvocationSourceKind.ClusterNode
                : InvocationSourceKind.Client;

    private Dictionary<string, LocalReminderService> BuildReminderServices(
        string[] allNodeNames,
        IGrainDirectory grainDirectory,
        IReadOnlyDictionary<string, InProcessRuntime> runtimes)
    {
        var reminderServices = new Dictionary<string, LocalReminderService>(StringComparer.Ordinal);
        if (_reminderTable is null)
        {
            return reminderServices;
        }

        foreach (var nodeName in allNodeNames)
        {
            var reminderService = new LocalReminderService(
                nodeName,
                _reminderTable,
                grainDirectory,
                _timeProvider,
                _reminderScanInterval);
            reminderService.Bind(runtimes[nodeName]);
            runtimes[nodeName].BindReminderService(reminderService);
            reminderServices.Add(nodeName, reminderService);
        }

        return reminderServices;
    }

    private Dictionary<string, MemoryStreamProvider> BuildMemoryStreamProviders(
        BinarySerializer serializer,
        InProcessRuntime runtime)
    {
        var providers = new Dictionary<string, MemoryStreamProvider>(StringComparer.Ordinal);
        foreach (var configuration in _memoryStreamProviders.Values.OrderBy(item => item.ProviderName, StringComparer.Ordinal))
        {
            var provider = new MemoryStreamProvider(configuration, serializer, _timeProvider);
            provider.Bind(runtime);
            providers.Add(configuration.ProviderName, provider);
        }

        return providers;
    }

    private Func<KernelHealthSnapshot> CreateHealthSnapshotProvider(
        string primaryNodeName,
        IClusterMembership membership,
        IReadOnlyDictionary<string, IActivationDirectory> activationDirectories,
        IReadOnlyDictionary<string, InProcessRuntime> runtimes)
        => () =>
        {
            var nodes = membership.GetMembers()
                .OrderBy(item => item.NodeName, StringComparer.Ordinal)
                .Select(member =>
                {
                    activationDirectories.TryGetValue(member.NodeName, out var activationDirectory);
                    runtimes.TryGetValue(member.NodeName, out var runtime);
                    return new KernelNodeHealthSnapshot(
                        member.NodeName,
                        member.HealthStatus,
                        activationDirectory?.GetActivationCount() ?? 0,
                        runtime?.GetResponseDispositionSnapshot()
                        ?? new ResponseDispositionSnapshot(0, 0, 0, 0, 0, 0, 0));
                })
                .ToArray();

            return new KernelHealthSnapshot(
                primaryNodeName,
                _timeProvider.GetUtcNow(),
                membership.CurrentEpoch,
                nodes.All(item => item.HealthStatus == NodeHealthStatus.Healthy),
                nodes);
        };

    private KernelHealthServer? StartHealthServerIfConfigured(Func<KernelHealthSnapshot> snapshotProvider)
    {
        if (string.IsNullOrWhiteSpace(_healthCheckUrlPrefix))
        {
            return null;
        }

        var healthServer = new KernelHealthServer(
            _healthCheckUrlPrefix,
            snapshotProvider,
            _loggerFactory ?? NullLoggerFactory.Instance);
        healthServer.Start();
        return healthServer;
    }

    private void RegisterGeneratedGrainImplementations()
    {
        foreach (var assembly in _generatedGrainImplementationAssemblies)
        {
            foreach (var implementationType in GetLoadableTypes(assembly))
            {
                if (implementationType is null || !implementationType.IsClass || implementationType.IsAbstract)
                {
                    continue;
                }

                foreach (var attribute in implementationType.GetCustomAttributes<GeneratedGrainImplementationAttribute>())
                {
                    var grainFactory = CreateGeneratedGrainFactory(implementationType, attribute.GrainType);

                    if (_grainImplementations.TryGetValue(attribute.GrainType, out var existingRegistration)
                        && !existingRegistration.IsGenerated)
                    {
                        continue;
                    }

                    var collectionAgeLimit = ResolveCollectionAgeLimit(attribute);
                    AddGrainImplementation(
                        attribute.GrainType,
                        grainFactory,
                        collectionAgeLimit,
                        attribute.PreferLocalPlacement,
                        attribute.InterleavableMethods,
                        isGenerated: true,
                        replaceExisting: false,
                        sourceDescription:
                        $"generated grain implementation '{implementationType.FullName}' in assembly '{assembly.GetName().Name}'");
                }
            }
        }
    }

    private void RegisterGeneratedGrainReferences()
    {
        foreach (var assembly in _generatedGrainReferenceAssemblies)
        {
            foreach (var generatedType in GetLoadableTypes(assembly))
            {
                if (generatedType is null || !generatedType.IsClass || generatedType.IsAbstract)
                {
                    continue;
                }

                foreach (var attribute in generatedType.GetCustomAttributes<GeneratedGrainReferenceAttribute>())
                {
                    ValidateGeneratedGrainReference(
                        generatedType,
                        attribute.ContractType,
                        attribute.GrainType);

                    if (_grainReferences.TryGetValue(attribute.ContractType, out var existingRegistration)
                        && !existingRegistration.IsGenerated)
                    {
                        continue;
                    }

                    var constructor = generatedType.GetConstructor([typeof(IInvocationRuntime), typeof(GrainId)]);
                    AddGrainReference(
                        attribute.ContractType,
                        attribute.GrainType,
                        (runtime, grainId) => constructor!.Invoke([runtime, grainId]),
                        isGenerated: true,
                        replaceExisting: false,
                        sourceDescription:
                        $"generated grain reference '{generatedType.FullName}' in assembly '{assembly.GetName().Name}'");
                }
            }
        }
    }

    private void RegisterGeneratedObjectReferences()
    {
        foreach (var assembly in _generatedObjectReferenceAssemblies)
        {
            foreach (var generatedType in GetLoadableTypes(assembly))
            {
                if (generatedType is null || !generatedType.IsClass || generatedType.IsAbstract)
                {
                    continue;
                }

                foreach (var attribute in generatedType.GetCustomAttributes<GeneratedObjectReferenceAttribute>())
                {
                    ValidateGeneratedObjectReference(generatedType, attribute.InterfaceType);

                    if (_objectReferenceRegistrations.TryGetValue(attribute.InterfaceType, out var existingRegistration)
                        && !existingRegistration.IsGenerated)
                    {
                        continue;
                    }

                    var constructor = generatedType.GetConstructor([typeof(IInvocationRuntime), typeof(GrainId)]);
                    AddObjectReference(
                        attribute.InterfaceType,
                        (runtime, grainId) => constructor!.Invoke([runtime, grainId]),
                        isGenerated: true,
                        replaceExisting: false,
                        sourceDescription:
                        $"generated object reference '{generatedType.FullName}' in assembly '{assembly.GetName().Name}'");
                }
            }
        }
    }

    private void AddObjectReference(
        Type contractType,
        Func<IInvocationRuntime, GrainId, object> referenceFactory,
        bool isGenerated,
        bool replaceExisting,
        string sourceDescription)
    {
        var registration = new ObjectReferenceRegistration(contractType, referenceFactory, isGenerated, sourceDescription);
        AddRegistration(
            _objectReferenceRegistrations, contractType, registration, replaceExisting,
            "object reference", contractType.FullName ?? contractType.Name);
    }

    private void AddGrainImplementation(
        string grainType,
        Func<GrainActivationContext, object> grainFactory,
        TimeSpan? collectionAgeLimit,
        bool preferLocalPlacement,
        IReadOnlyCollection<string>? interleavableMethods,
        bool isGenerated,
        bool replaceExisting,
        string sourceDescription)
    {
        var normalizedMethods = interleavableMethods?
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        var registration = new GrainImplementationRegistration(
            grainType, grainFactory, collectionAgeLimit, preferLocalPlacement,
            normalizedMethods, isGenerated, sourceDescription);
        AddRegistration(
            _grainImplementations, grainType, registration, replaceExisting,
            "grain implementation", grainType);
    }

    private void AddGrainReference(
        Type contractType,
        string grainType,
        Func<IInvocationRuntime, GrainId, object> referenceFactory,
        bool isGenerated,
        bool replaceExisting,
        string sourceDescription)
    {
        var registration = new GrainReferenceRegistration(
            contractType, grainType, referenceFactory, isGenerated, sourceDescription);
        AddRegistration(
            _grainReferences, contractType, registration, replaceExisting,
            "grain reference", contractType.FullName ?? contractType.Name);
    }

    private static void AddRegistration<TKey, TValue>(
        Dictionary<TKey, TValue> registry,
        TKey key,
        TValue value,
        bool replaceExisting,
        string registrationKind,
        string displayKey)
        where TKey : notnull
        where TValue : IRegistrationEntry
    {
        if (replaceExisting)
        {
            registry[key] = value;
            return;
        }

        if (!registry.TryGetValue(key, out var existing))
        {
            registry.Add(key, value);
            return;
        }

        if (existing.IsGenerated && value.IsGenerated
            && string.Equals(existing.SourceDescription, value.SourceDescription, StringComparison.Ordinal))
        {
            return;
        }

        if (existing.IsGenerated && value.IsGenerated)
        {
            throw new InvalidOperationException(
                $"Multiple generated {registrationKind} registrations were found for '{displayKey}': '{existing.SourceDescription}' and '{value.SourceDescription}'.");
        }

        throw new InvalidOperationException(
            $"{char.ToUpperInvariant(registrationKind[0])}{registrationKind[1..]} '{displayKey}' is already registered by '{existing.SourceDescription}'.");
    }

    private static void ValidateGeneratedObjectReference(Type generatedType, Type interfaceType)
    {
        if (!typeof(IObjectReference).IsAssignableFrom(generatedType))
        {
            throw new InvalidOperationException(
                $"Generated object reference '{generatedType.FullName}' must implement '{nameof(IObjectReference)}'.");
        }

        if (!interfaceType.IsAssignableFrom(generatedType))
        {
            throw new InvalidOperationException(
                $"Generated object reference '{generatedType.FullName}' must implement '{interfaceType.FullName}'.");
        }

        if (generatedType.GetConstructor([typeof(IInvocationRuntime), typeof(GrainId)]) is null)
        {
            throw new InvalidOperationException(
                $"Generated object reference '{generatedType.FullName}' must expose a public constructor '(IInvocationRuntime, GrainId)'.");
        }
    }

    private static void ValidateGeneratedGrainReference(Type generatedType, Type contractType, string grainType)
    {
        if (!contractType.IsInterface)
        {
            throw new InvalidOperationException(
                $"Generated grain reference contract '{contractType.FullName}' must be an interface.");
        }

        if (!contractType.IsAssignableFrom(generatedType))
        {
            throw new InvalidOperationException(
                $"Generated grain reference '{generatedType.FullName}' must implement '{contractType.FullName}'.");
        }

        if (string.IsNullOrWhiteSpace(grainType))
        {
            throw new InvalidOperationException(
                $"Generated grain reference '{generatedType.FullName}' must declare a non-empty grain type.");
        }

        if (generatedType.GetConstructor([typeof(IInvocationRuntime), typeof(GrainId)]) is null)
        {
            throw new InvalidOperationException(
                $"Generated grain reference '{generatedType.FullName}' must expose a public constructor '(IInvocationRuntime, GrainId)'.");
        }
    }

    private static Func<GrainActivationContext, object> CreateGeneratedGrainFactory(Type implementationType, string grainType)
    {
        if (string.IsNullOrWhiteSpace(grainType))
        {
            throw new InvalidOperationException(
                $"Generated grain implementation '{implementationType.FullName}' must declare a non-empty grain type.");
        }

        var supportedConstructors = implementationType
            .GetConstructors()
            .Select(TryCreateGeneratedGrainFactory)
            .Where(item => item is not null)
            .ToArray();
        if (supportedConstructors.Length == 0)
        {
            throw new InvalidOperationException(
                $"Generated grain implementation '{implementationType.FullName}' must expose either a public parameterless constructor or a single public constructor whose parameters are annotated persistent or transactional states.");
        }

        if (supportedConstructors.Length > 1)
        {
            throw new InvalidOperationException(
                $"Generated grain implementation '{implementationType.FullName}' exposes multiple supported public constructors. Declare a single activator constructor.");
        }

        return supportedConstructors[0]!;
    }

    private static Func<GrainActivationContext, object>? TryCreateGeneratedGrainFactory(ConstructorInfo constructor)
    {
        var parameters = constructor.GetParameters();
        if (parameters.Length == 0)
        {
            return _ => constructor.Invoke([])!;
        }

        var resolvers = new Func<GrainActivationContext, object?>[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            if (!TryCreateConstructorParameterResolver(parameter, out var resolver))
            {
                return null;
            }

            resolvers[index] = resolver;
        }

        return context =>
        {
            var arguments = new object?[resolvers.Length];
            for (var index = 0; index < resolvers.Length; index++)
            {
                arguments[index] = resolvers[index](context);
            }

            return constructor.Invoke(arguments)!;
        };
    }

    private static bool TryCreateConstructorParameterResolver(
        ParameterInfo parameter,
        out Func<GrainActivationContext, object?> resolver)
    {
        resolver = default!;

        if (!parameter.ParameterType.IsGenericType)
        {
            return false;
        }

        var genericTypeDefinition = parameter.ParameterType.GetGenericTypeDefinition();
        if (genericTypeDefinition == typeof(IPersistentState<>))
        {
            var attribute = parameter.GetCustomAttribute<PersistentStateAttribute>();
            if (attribute is null)
            {
                throw new InvalidOperationException(
                    $"Persistent state parameter '{parameter.Name}' on generated grain implementation constructor must declare [{nameof(PersistentStateAttribute)}].");
            }

            var stateName = string.IsNullOrWhiteSpace(attribute.StateName)
                ? parameter.Name
                : attribute.StateName;
            if (string.IsNullOrWhiteSpace(stateName))
            {
                throw new InvalidOperationException(
                    "Persistent state constructor parameters must declare a state name or use a non-empty parameter name.");
            }

            var stateType = parameter.ParameterType.GetGenericArguments()[0];
            var resolveMethod = typeof(GrainActivationContext)
                .GetMethod(nameof(GrainActivationContext.ResolvePersistentState))
                ?.MakeGenericMethod(stateType)
                ?? throw new InvalidOperationException("Persistent state activation context is missing its resolver.");

            resolver = context => resolveMethod.Invoke(context, [stateName, attribute.StorageName]);
            return true;
        }

        if (genericTypeDefinition != typeof(ITransactionalState<>))
        {
            return false;
        }

        var transactionalAttribute = parameter.GetCustomAttribute<TransactionalStateAttribute>();
        if (transactionalAttribute is null)
        {
            throw new InvalidOperationException(
                $"Transactional state parameter '{parameter.Name}' on generated grain implementation constructor must declare [{nameof(TransactionalStateAttribute)}].");
        }

        var transactionalStateName = string.IsNullOrWhiteSpace(transactionalAttribute.StateName)
            ? parameter.Name
            : transactionalAttribute.StateName;
        if (string.IsNullOrWhiteSpace(transactionalStateName))
        {
            throw new InvalidOperationException(
                "Transactional state constructor parameters must declare a state name or use a non-empty parameter name.");
        }

        var transactionalStateType = parameter.ParameterType.GetGenericArguments()[0];
        var transactionalResolveMethod = typeof(GrainActivationContext)
            .GetMethod(nameof(GrainActivationContext.ResolveTransactionalState))
            ?.MakeGenericMethod(transactionalStateType)
            ?? throw new InvalidOperationException("Transactional state activation context is missing its resolver.");

        resolver = context => transactionalResolveMethod.Invoke(
            context,
            [transactionalStateName, transactionalAttribute.StorageName]);
        return true;
    }

    private static TimeSpan? ResolveCollectionAgeLimit(GeneratedGrainImplementationAttribute attribute)
    {
        if (attribute.CollectionAgeLimitMilliseconds < -1)
        {
            throw new InvalidOperationException(
                $"Generated grain implementation '{attribute.GrainType}' declares an invalid collection age limit '{attribute.CollectionAgeLimitMilliseconds}'.");
        }

        return attribute.CollectionAgeLimitMilliseconds >= 0
            ? TimeSpan.FromMilliseconds(attribute.CollectionAgeLimitMilliseconds)
            : null;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null)!;
        }
    }

    private interface IRegistrationEntry
    {
        bool IsGenerated { get; }
        string SourceDescription { get; }
    }

    private sealed record GrainImplementationRegistration(
        string GrainType,
        Func<GrainActivationContext, object> GrainFactory,
        TimeSpan? CollectionAgeLimit,
        bool PreferLocalPlacement,
        IReadOnlyList<string> InterleavableMethods,
        bool IsGenerated,
        string SourceDescription) : IRegistrationEntry;

    private sealed record GrainReferenceRegistration(
        Type ContractType,
        string GrainType,
        Func<IInvocationRuntime, GrainId, object> ReferenceFactory,
        bool IsGenerated,
        string SourceDescription) : IRegistrationEntry;

    private sealed record ObjectReferenceRegistration(
        Type ContractType,
        Func<IInvocationRuntime, GrainId, object> ReferenceFactory,
        bool IsGenerated,
        string SourceDescription) : IRegistrationEntry;

    private sealed record GrainPolicySet(
        Dictionary<string, Func<GrainActivationContext, object>> Factories,
        Dictionary<string, GrainTypeCollectionPolicy> CollectionPolicies,
        Dictionary<string, GrainTypePlacementHint> PlacementHints,
        Dictionary<string, GrainTypeSchedulingPolicy> SchedulingPolicies);
}
