using System.Reflection;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Routing;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Scheduling;

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
            () => grainFactory(),
            collectionAgeLimit: null,
            preferLocalPlacement: false,
            interleavableMethods: null,
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

        var grainPolicies = BuildGrainPolicies();
        var allNodeNames = ResolveNodeNames(nodeName, peerNodeNames);
        var (membership, membershipViews, membershipGossiper) = BuildMembership(allNodeNames);
        var nodeRegistry = new InProcessNodeRegistry();
        var failureDetector = new ConsecutiveFailureDetector(membership);
        var activationDirectories = new Dictionary<string, IActivationDirectory>(StringComparer.Ordinal);
        var (grainDirectory, loadProvider, rebalancingPolicy) =
            BuildDirectory(nodeName, membershipViews[nodeName], grainPolicies.PlacementHints, activationDirectories);
        var probeService = new InProcessClusterProbeService(nodeName, membership, nodeRegistry, failureDetector);
        var objectReferenceFactoryRegistry = BuildObjectReferenceFactoryRegistry();
        var (locators, callbackDirectories, runtimes) =
            BuildNodeRuntimes(allNodeNames, grainPolicies, membershipViews, nodeRegistry, failureDetector,
                grainDirectory, objectReferenceFactoryRegistry, activationDirectories);

        var bindings = _grainReferences.ToDictionary(
            item => item.Key,
            item => new OrleansReplicaKernelRegistration(item.Value.GrainType, item.Value.ReferenceFactory));

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

    private GrainPolicySet BuildGrainPolicies() => new(
        _grainImplementations.Values.ToDictionary(
            item => item.GrainType, item => item.GrainFactory, StringComparer.Ordinal),
        _grainImplementations.Values.ToDictionary(
            item => item.GrainType, item => new GrainTypeCollectionPolicy(item.CollectionAgeLimit), StringComparer.Ordinal),
        _grainImplementations.Values.ToDictionary(
            item => item.GrainType, item => new GrainTypePlacementHint(item.PreferLocalPlacement), StringComparer.Ordinal),
        _grainImplementations.Values.ToDictionary(
            item => item.GrainType, item => new GrainTypeSchedulingPolicy(item.InterleavableMethods), StringComparer.Ordinal));

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

    private (InProcessClusterMembership Membership,
        Dictionary<string, GossipedClusterMembershipView> Views,
        InProcessMembershipGossiper Gossiper) BuildMembership(string[] allNodeNames)
    {
        InProcessClusterMembership membership;
        Dictionary<string, GossipedClusterMembershipView> views;

        if (_membershipCheckpoint is null)
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

    private (InMemoryGrainDirectory Directory,
        ActivationDirectoryLoadProvider LoadProvider,
        LoadSkewRebalancingPolicy RebalancingPolicy) BuildDirectory(
        string nodeName,
        IClusterMembershipView membershipView,
        IReadOnlyDictionary<string, GrainTypePlacementHint> placementHints,
        Dictionary<string, IActivationDirectory> activationDirectories)
    {
        var loadProvider = new ActivationDirectoryLoadProvider(activationDirectories);
        var placementPolicy = new LeastLoadedPlacementPolicy(nodeName);
        var relocationPolicy = new HealthyNodeRelocationPolicy(nodeName);
        var rebalancingPolicy = new LoadSkewRebalancingPolicy(minimumSkew: 1);

        var directory = _runtimeCheckpoint is null
            ? new InMemoryGrainDirectory(membershipView, placementPolicy, loadProvider, relocationPolicy, placementHints)
            : InMemoryGrainDirectory.Restore(membershipView, placementPolicy, loadProvider, relocationPolicy,
                _runtimeCheckpoint.GrainDirectory, placementHints);

        return (directory, loadProvider, rebalancingPolicy);
    }

    private ObjectReferenceFactoryRegistry BuildObjectReferenceFactoryRegistry() =>
        new(_objectReferenceRegistrations.ToDictionary(
            item => ObjectReferenceFactoryRegistry.GetInterfaceNameFromType(item.Key),
            item => item.Value.ReferenceFactory,
            StringComparer.Ordinal));

    private (Dictionary<string, IGrainLocator> Locators,
        Dictionary<string, LocalCallbackDirectory> CallbackDirectories,
        Dictionary<string, InProcessRuntime> Runtimes) BuildNodeRuntimes(
        string[] allNodeNames,
        GrainPolicySet grainPolicies,
        Dictionary<string, GossipedClusterMembershipView> membershipViews,
        InProcessNodeRegistry nodeRegistry,
        IFailureDetector failureDetector,
        IGrainDirectory grainDirectory,
        ObjectReferenceFactoryRegistry objectReferenceFactoryRegistry,
        Dictionary<string, IActivationDirectory> activationDirectories)
    {
        var locators = new Dictionary<string, IGrainLocator>(StringComparer.Ordinal);
        var callbackDirectories = new Dictionary<string, LocalCallbackDirectory>(StringComparer.Ordinal);
        var runtimes = new Dictionary<string, InProcessRuntime>(StringComparer.Ordinal);

        foreach (var currentNodeName in allNodeNames)
        {
            var transport = new InProcessMessageTransport(membershipViews[currentNodeName], nodeRegistry, _timeProvider);
            var locator = new DirectoryGrainLocator(grainDirectory);
            var callbackDirectory = new LocalCallbackDirectory(_timeProvider);
            var activationCheckpoint = _runtimeCheckpoint?.ActivationDirectories
                .FirstOrDefault(item => string.Equals(item.NodeName, currentNodeName, StringComparison.Ordinal));
            var activationDirectory = activationCheckpoint is null
                ? new LocalActivationDirectory(
                    grainPolicies.Factories, grainPolicies.CollectionPolicies,
                    callbackDirectory, grainPolicies.SchedulingPolicies, _timeProvider)
                : LocalActivationDirectory.Restore(
                    grainPolicies.Factories, grainPolicies.CollectionPolicies,
                    callbackDirectory, grainPolicies.SchedulingPolicies, _timeProvider, activationCheckpoint);
            var router = new LocalGrainRouter(currentNodeName, locator);
            var runtime = new InProcessRuntime(
                currentNodeName, failureDetector, locator, router, activationDirectory,
                transport, objectReferenceFactoryRegistry, _timeProvider, _responseHistoryRetention);

            locators.Add(currentNodeName, locator);
            activationDirectories.Add(currentNodeName, activationDirectory);
            callbackDirectories.Add(currentNodeName, callbackDirectory);
            runtimes.Add(currentNodeName, runtime);
            nodeRegistry.Register(currentNodeName, runtime, runtime);
        }

        return (locators, callbackDirectories, runtimes);
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
        Func<object> grainFactory,
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

    private interface IRegistrationEntry
    {
        bool IsGenerated { get; }
        string SourceDescription { get; }
    }

    private sealed record GrainImplementationRegistration(
        string GrainType,
        Func<object> GrainFactory,
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
        Dictionary<string, Func<object>> Factories,
        Dictionary<string, GrainTypeCollectionPolicy> CollectionPolicies,
        Dictionary<string, GrainTypePlacementHint> PlacementHints,
        Dictionary<string, GrainTypeSchedulingPolicy> SchedulingPolicies);
}
