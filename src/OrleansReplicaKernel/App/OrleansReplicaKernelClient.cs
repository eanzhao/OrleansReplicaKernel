using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Routing;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.App;

public sealed class OrleansReplicaKernelClient : IAsyncDisposable
{
    private readonly string _nodeName;
    private readonly IInvocationRuntime _runtime;
    private readonly LocalCallbackDirectory _callbackDirectory;
    private readonly ObjectReferenceFactoryRegistry _objectReferenceFactoryRegistry;
    private readonly RoundRobinGatewaySelector _gatewaySelector;
    private readonly IReadOnlyList<IAsyncDisposable> _managedResources;
    private readonly IReadOnlyDictionary<Type, OrleansReplicaKernelRegistration> _registrations;

    internal OrleansReplicaKernelClient(
        string nodeName,
        TimeProvider timeProvider,
        IInvocationRuntime runtime,
        LocalCallbackDirectory callbackDirectory,
        ObjectReferenceFactoryRegistry objectReferenceFactoryRegistry,
        RoundRobinGatewaySelector gatewaySelector,
        IReadOnlyList<IAsyncDisposable> managedResources,
        IReadOnlyDictionary<Type, OrleansReplicaKernelRegistration> registrations)
    {
        _nodeName = nodeName;
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _callbackDirectory = callbackDirectory ?? throw new ArgumentNullException(nameof(callbackDirectory));
        _objectReferenceFactoryRegistry = objectReferenceFactoryRegistry ?? throw new ArgumentNullException(nameof(objectReferenceFactoryRegistry));
        _gatewaySelector = gatewaySelector ?? throw new ArgumentNullException(nameof(gatewaySelector));
        _managedResources = managedResources ?? throw new ArgumentNullException(nameof(managedResources));
        _registrations = registrations ?? throw new ArgumentNullException(nameof(registrations));
    }

    public TimeProvider TimeProvider { get; }

    public TContract GetGrain<TContract>(string key)
        where TContract : class
    {
        if (!_registrations.TryGetValue(typeof(TContract), out var registrationObject))
        {
            throw new InvalidOperationException($"No grain contract registered for {typeof(TContract).Name}.");
        }

        var grainId = new GrainId(registrationObject.GrainType, key);

        TraceLog.Write("client", $"get grain {typeof(TContract).Name} -> {grainId}");
        return (TContract)registrationObject.ReferenceFactory(_runtime, grainId);
    }

    public CallbackLease<THandle> RegisterCallbackTarget<THandle>(
        string callbackType,
        object implementation,
        Func<GrainId, THandle> handleFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackType);
        ArgumentNullException.ThrowIfNull(implementation);
        ArgumentNullException.ThrowIfNull(handleFactory);

        var gatewayNodeName = _gatewaySelector.SelectNextGateway();
        var callbackGrainId = _callbackDirectory.Register(
            gatewayNodeName,
            callbackType,
            implementation,
            _nodeName);
        var handle = handleFactory(callbackGrainId);

        return new CallbackLease<THandle>(
            callbackGrainId,
            handle,
            ReleaseCallbackAsync);
    }

    public CallbackLease<TObserver> CreateObserverReference<TObserver>(
        string callbackType,
        TObserver implementation,
        Func<IInvocationRuntime, GrainId, TObserver> referenceFactory)
        where TObserver : class
        => RegisterCallbackTarget(
            callbackType,
            implementation!,
            grainId => referenceFactory(_runtime, grainId));

    public CallbackLease<TObjectReference> CreateObjectReference<TObjectReference>(
        string callbackType,
        object implementation)
        where TObjectReference : class
        => RegisterCallbackTarget(
            callbackType,
            implementation,
            grainId => _objectReferenceFactoryRegistry.Create<TObjectReference>(_runtime, grainId));

    public async ValueTask DisposeAsync()
    {
        foreach (var resource in _managedResources)
        {
            await resource.DisposeAsync();
        }
    }

    private async ValueTask ReleaseCallbackAsync(GrainId grainId)
    {
        await _callbackDirectory.UnregisterAsync(grainId);
    }
}
