using System.Reflection;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Routing;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.App;

public sealed class OrleansReplicaKernelBuilder
{
    private readonly Dictionary<string, GrainImplementationRegistration> _grainImplementations = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, GrainReferenceRegistration> _grainReferences = new();
    private readonly Dictionary<Type, ObjectReferenceRegistration> _objectReferenceRegistrations = new();
    private readonly HashSet<Assembly> _generatedGrainImplementationAssemblies = [];
    private readonly HashSet<Assembly> _generatedGrainReferenceAssemblies = [];
    private readonly HashSet<Assembly> _generatedObjectReferenceAssemblies = [];
    private TimeSpan _membershipStabilizationWindow = TimeSpan.FromMilliseconds(200);
    private int _membershipGossipFanout = 1;
    private int _membershipAntiEntropyInterval = 4;
    private TimeProvider _timeProvider = TimeProvider.System;
    private TimeSpan _responseHistoryRetention = TimeSpan.FromMinutes(5);
    private OrleansReplicaKernelMembershipCheckpoint? _membershipCheckpoint;
    private OrleansReplicaKernelRuntimeCheckpoint? _runtimeCheckpoint;

    public OrleansReplicaKernelBuilder AddGrain<TContract, TGrain>(
        string grainType,
        Func<TGrain> grainFactory,
        Func<IInvocationRuntime, GrainId, TContract> referenceFactory)
        where TContract : class
        where TGrain : class
    {
        AddGrainImplementation(
            grainType,
            () => grainFactory(),
            collectionAgeLimit: null,
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
            () => grainFactory(),
            collectionAgeLimit: null,
            isGenerated: false,
            replaceExisting: false,
            sourceDescription: $"manual grain implementation for '{grainType}'");

        return this;
    }

    public OrleansReplicaKernelBuilder AddGeneratedGrainImplementationsFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        _generatedGrainImplementationAssemblies.Add(assembly);
        return this;
    }

    public OrleansReplicaKernelBuilder AddGeneratedGrainReferencesFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        _generatedGrainReferenceAssemblies.Add(assembly);
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
        RegisterGeneratedGrainImplementations();
        RegisterGeneratedGrainReferences();
        RegisterGeneratedObjectReferences();

        var requestedNodeNames = new[] { nodeName }
            .Concat(peerNodeNames)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var grainFactories = _grainImplementations.Values.ToDictionary(
            item => item.GrainType,
            item => item.GrainFactory,
            StringComparer.Ordinal);
        var grainCollectionPolicies = _grainImplementations.Values.ToDictionary(
            item => item.GrainType,
            item => new GrainTypeCollectionPolicy(item.CollectionAgeLimit),
            StringComparer.Ordinal);

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
                    _membershipStabilizationWindow,
                    _timeProvider),
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
                        _membershipStabilizationWindow,
                        _timeProvider);
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
        var callbackDirectories = new Dictionary<string, LocalCallbackDirectory>(StringComparer.Ordinal);
        var objectReferenceFactoryRegistry = new ObjectReferenceFactoryRegistry(
            _objectReferenceRegistrations.ToDictionary(
                item => ObjectReferenceFactoryRegistry.GetInterfaceNameFromType(item.Key),
                item => item.Value.ReferenceFactory,
                StringComparer.Ordinal));

        foreach (var currentNodeName in allNodeNames)
        {
            var transport = new InProcessMessageTransport(membershipViews[currentNodeName], nodeRegistry);
            var locator = new DirectoryGrainLocator(grainDirectory);
            var callbackDirectory = new LocalCallbackDirectory();
            var activationCheckpoint = runtimeCheckpoint?.ActivationDirectories
                .FirstOrDefault(item => string.Equals(item.NodeName, currentNodeName, StringComparison.Ordinal));
            var activationDirectory = activationCheckpoint is null
                ? new LocalActivationDirectory(grainFactories, grainCollectionPolicies, callbackDirectory)
                : LocalActivationDirectory.Restore(
                    grainFactories,
                    grainCollectionPolicies,
                    callbackDirectory,
                    activationCheckpoint);
            var router = new LocalGrainRouter(currentNodeName, locator);
            var runtime = new InProcessRuntime(
                currentNodeName,
                failureDetector,
                locator,
                router,
                activationDirectory,
                transport,
                objectReferenceFactoryRegistry,
                _timeProvider,
                _responseHistoryRetention);

            locators.Add(currentNodeName, locator);
            activationDirectories.Add(currentNodeName, activationDirectory);
            callbackDirectories.Add(currentNodeName, callbackDirectory);
            runtimes.Add(currentNodeName, runtime);
            nodeRegistry.Register(currentNodeName, runtime, runtime);
        }

        var bindings = _grainReferences.ToDictionary(
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
            callbackDirectories,
            objectReferenceFactoryRegistry,
            runtimes,
            runtimes.Values.Cast<IAsyncDisposable>()
                .Concat(callbackDirectories.Values)
                .ToArray(),
            bindings);
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
                    ValidateGeneratedGrainImplementation(implementationType, attribute.GrainType);

                    if (_grainImplementations.TryGetValue(attribute.GrainType, out var existingRegistration)
                        && !existingRegistration.IsGenerated)
                    {
                        continue;
                    }

                    var constructor = implementationType.GetConstructor(Type.EmptyTypes);
                    var collectionAgeLimit = ResolveCollectionAgeLimit(attribute);
                    AddGrainImplementation(
                        attribute.GrainType,
                        () => constructor!.Invoke([])!,
                        collectionAgeLimit,
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
        if (replaceExisting)
        {
            _objectReferenceRegistrations[contractType] = new ObjectReferenceRegistration(
                contractType,
                referenceFactory,
                isGenerated,
                sourceDescription);
            return;
        }

        if (_objectReferenceRegistrations.TryGetValue(contractType, out var existingRegistration))
        {
            if (existingRegistration.IsGenerated
                && isGenerated
                && string.Equals(existingRegistration.SourceDescription, sourceDescription, StringComparison.Ordinal))
            {
                return;
            }

            if (existingRegistration.IsGenerated && isGenerated)
            {
                throw new InvalidOperationException(
                    $"Multiple generated object reference registrations were found for '{contractType.FullName}': '{existingRegistration.SourceDescription}' and '{sourceDescription}'.");
            }

            throw new InvalidOperationException(
                $"Object reference '{contractType.FullName}' is already registered by '{existingRegistration.SourceDescription}'.");
        }

        _objectReferenceRegistrations.Add(
            contractType,
            new ObjectReferenceRegistration(
                contractType,
                referenceFactory,
                isGenerated,
                sourceDescription));
    }

    private void AddGrainImplementation(
        string grainType,
        Func<object> grainFactory,
        TimeSpan? collectionAgeLimit,
        bool isGenerated,
        bool replaceExisting,
        string sourceDescription)
    {
        if (replaceExisting)
        {
            _grainImplementations[grainType] = new GrainImplementationRegistration(
                grainType,
                grainFactory,
                collectionAgeLimit,
                isGenerated,
                sourceDescription);
            return;
        }

        if (_grainImplementations.TryGetValue(grainType, out var existingRegistration))
        {
            if (existingRegistration.IsGenerated
                && isGenerated
                && string.Equals(existingRegistration.SourceDescription, sourceDescription, StringComparison.Ordinal))
            {
                return;
            }

            if (existingRegistration.IsGenerated && isGenerated)
            {
                throw new InvalidOperationException(
                    $"Multiple generated grain implementation registrations were found for '{grainType}': '{existingRegistration.SourceDescription}' and '{sourceDescription}'.");
            }

            throw new InvalidOperationException(
                $"Grain implementation '{grainType}' is already registered by '{existingRegistration.SourceDescription}'.");
        }

        _grainImplementations.Add(
            grainType,
            new GrainImplementationRegistration(
                grainType,
                grainFactory,
                collectionAgeLimit,
                isGenerated,
                sourceDescription));
    }

    private void AddGrainReference(
        Type contractType,
        string grainType,
        Func<IInvocationRuntime, GrainId, object> referenceFactory,
        bool isGenerated,
        bool replaceExisting,
        string sourceDescription)
    {
        if (replaceExisting)
        {
            _grainReferences[contractType] = new GrainReferenceRegistration(
                contractType,
                grainType,
                referenceFactory,
                isGenerated,
                sourceDescription);
            return;
        }

        if (_grainReferences.TryGetValue(contractType, out var existingRegistration))
        {
            if (existingRegistration.IsGenerated
                && isGenerated
                && string.Equals(existingRegistration.SourceDescription, sourceDescription, StringComparison.Ordinal))
            {
                return;
            }

            if (existingRegistration.IsGenerated && isGenerated)
            {
                throw new InvalidOperationException(
                    $"Multiple generated grain reference registrations were found for '{contractType.FullName}': '{existingRegistration.SourceDescription}' and '{sourceDescription}'.");
            }

            throw new InvalidOperationException(
                $"Grain reference '{contractType.FullName}' is already registered by '{existingRegistration.SourceDescription}'.");
        }

        _grainReferences.Add(
            contractType,
            new GrainReferenceRegistration(
                contractType,
                grainType,
                referenceFactory,
                isGenerated,
                sourceDescription));
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

    private static void ValidateGeneratedGrainImplementation(Type implementationType, string grainType)
    {
        if (string.IsNullOrWhiteSpace(grainType))
        {
            throw new InvalidOperationException(
                $"Generated grain implementation '{implementationType.FullName}' must declare a non-empty grain type.");
        }

        if (implementationType.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new InvalidOperationException(
                $"Generated grain implementation '{implementationType.FullName}' must expose a public parameterless constructor.");
        }
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

    private sealed record GrainImplementationRegistration(
        string GrainType,
        Func<object> GrainFactory,
        TimeSpan? CollectionAgeLimit,
        bool IsGenerated,
        string SourceDescription);

    private sealed record GrainReferenceRegistration(
        Type ContractType,
        string GrainType,
        Func<IInvocationRuntime, GrainId, object> ReferenceFactory,
        bool IsGenerated,
        string SourceDescription);

    private sealed record ObjectReferenceRegistration(
        Type ContractType,
        Func<IInvocationRuntime, GrainId, object> ReferenceFactory,
        bool IsGenerated,
        string SourceDescription);
}
