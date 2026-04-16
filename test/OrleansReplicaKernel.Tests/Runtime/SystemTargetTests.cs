using System.Reflection;
using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Routing;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Security;
using OrleansReplicaKernel.Versioning;

namespace OrleansReplicaKernel.Tests.Runtime;

public sealed class SystemTargetTests
{
    [Fact]
    public void SystemTargetId_RoundTripsThroughGrainId()
    {
        var id = new SystemTargetId("management", "node-b");

        var grainId = id.ToGrainId();
        var parsed = SystemTargetId.FromGrainId(grainId);

        Assert.Equal(new GrainId("$system/management", "node-b"), grainId);
        Assert.True(SystemTargetId.IsSystemTarget(grainId));
        Assert.Equal(id, parsed);
    }

    [Fact]
    public void LocalGrainRouter_RoutesSystemTargetsByEncodedNodeWithoutLocatorLookup()
    {
        var locator = new ThrowingLocator();
        var router = new LocalGrainRouter("node-a", locator);
        var grainId = new SystemTargetId("identity", "node-b").ToGrainId();

        var address = router.Route(CreateMessage("node-a", grainId, new NoOpInvokable()));

        Assert.Equal("node-b", address.NodeName);
        Assert.Equal(grainId, address.GrainId);
        Assert.Equal(0, address.OwnerVersion);
        Assert.Equal(0, locator.LocateCallCount);
    }

    [Fact]
    public async Task SystemTargetsOnDifferentNodes_AreDifferentInstances()
    {
        await using var nodeA = CreateRuntime("node-a", new IdentitySystemTarget(), IdentitySystemTarget.TargetType);
        await using var nodeB = CreateRuntime("node-b", new IdentitySystemTarget(), IdentitySystemTarget.TargetType);

        var instanceA = await nodeA.Runtime.InvokeAsync<string>(
            new SystemTargetId(IdentitySystemTarget.TargetType, "node-a").ToGrainId(),
            new GetIdentityInvokable());
        var instanceB = await nodeB.Runtime.InvokeAsync<string>(
            new SystemTargetId(IdentitySystemTarget.TargetType, "node-b").ToGrainId(),
            new GetIdentityInvokable());

        Assert.NotEqual(instanceA, instanceB);
        Assert.Equal(0, nodeA.Locator.LocateCallCount);
        Assert.Equal(0, nodeB.Locator.LocateCallCount);
    }

    [Fact]
    public void LocalGrainRouter_SystemTargetRouting_BypassesPlacementPolicy()
    {
        var membershipView = new StaticClusterMembershipView("node-a", ["node-a", "node-b"]);
        var directory = new InMemoryGrainDirectory(
            membershipView,
            new ThrowingPlacementPolicy(),
            new StaticLoadProvider(),
            new HealthyNodeRelocationPolicy("node-a"));
        var locator = new DirectoryGrainLocator(directory);
        var router = new LocalGrainRouter("node-a", locator);
        var grainId = new SystemTargetId("management", "node-b").ToGrainId();

        var address = router.Route(CreateMessage("node-a", grainId, new NoOpInvokable()));

        Assert.Equal("node-b", address.NodeName);
        Assert.Equal(grainId, address.GrainId);
        Assert.Equal(0, address.OwnerVersion);
    }

    [Fact]
    public async Task Builder_RegistersManagementSystemTargetOnEachNode()
    {
        await using var host = new OrleansReplicaKernelBuilder().Build("node-a", "node-b");
        var runtimes = GetRuntimes(host);

        var summaryA = await runtimes["node-a"].InvokeAsync<string>(
            ManagementSystemTarget.CreateId("node-a").ToGrainId(),
            new GetMembershipViewSummaryInvokable());
        var summaryB = await runtimes["node-b"].InvokeAsync<string>(
            ManagementSystemTarget.CreateId("node-b").ToGrainId(),
            new GetMembershipViewSummaryInvokable());

        Assert.Contains("observer=node-a", summaryA, StringComparison.Ordinal);
        Assert.Contains("observer=node-b", summaryB, StringComparison.Ordinal);
        Assert.NotEqual(summaryA, summaryB);
    }

    private static InvocationMessage CreateMessage(
        string sourceNodeName,
        GrainId grainId,
        IInvokable invokable)
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            sourceNodeName,
            new GrainAddress(sourceNodeName, grainId, OwnerVersion: 0),
            invokable,
            InvocationSourceKind.ClusterNode,
            InvocationIdentity.CreateLocal(sourceNodeName, InvocationSourceKind.ClusterNode));

    private static RuntimeHarness CreateRuntime(
        string nodeName,
        ISystemTarget systemTarget,
        string targetType)
    {
        var locator = new ThrowingLocator();
        var systemTargetDirectory = new SystemTargetDirectory(nodeName);
        systemTargetDirectory.Register(new SystemTargetId(targetType, nodeName), systemTarget);
        var activationDirectory = new LocalActivationDirectory(
            nodeName,
            new Dictionary<string, Func<object>>(StringComparer.Ordinal),
            new Dictionary<string, GrainTypeCollectionPolicy>(StringComparer.Ordinal),
            new LocalCallbackDirectory(),
            timeProvider: TimeProvider.System,
            systemTargetDirectory: systemTargetDirectory);
        var runtime = new InProcessRuntime(
            nodeName,
            new NoOpFailureDetector(),
            locator,
            new LocalGrainRouter(nodeName, locator),
            activationDirectory,
            new ThrowingTransport(),
            new ObjectReferenceFactoryRegistry(
                new Dictionary<string, Func<IInvocationRuntime, GrainId, object>>(StringComparer.Ordinal)));

        return new RuntimeHarness(runtime, locator);
    }

    private static IReadOnlyDictionary<string, InProcessRuntime> GetRuntimes(OrleansReplicaKernelHost host)
    {
        var field = typeof(OrleansReplicaKernelHost).GetField("_runtimes", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to resolve host runtime registry.");
        return (IReadOnlyDictionary<string, InProcessRuntime>)(field.GetValue(host)
            ?? throw new InvalidOperationException("Host runtime registry is not available."));
    }

    private sealed class NoOpInvokable : IInvokable
    {
        public string InterfaceName => nameof(NoOpInvokable);

        public string InterfaceCompatibilityFamily => "test.system-target.noop";

        public int InterfaceVersion => 1;

        public string MethodName => nameof(NoOpInvokable);

        public ValueTask<object?> InvokeAsync(object target, CancellationToken cancellationToken)
            => ValueTask.FromResult<object?>(null);
    }

    private sealed class GetIdentityInvokable : IInvokable
    {
        public string InterfaceName => nameof(IdentitySystemTarget);

        public string InterfaceCompatibilityFamily => "test.system-target.identity";

        public int InterfaceVersion => 1;

        public string MethodName => nameof(IdentitySystemTarget.GetInstanceId);

        public ValueTask<object?> InvokeAsync(object target, CancellationToken cancellationToken)
            => ValueTask.FromResult<object?>(((IdentitySystemTarget)target).GetInstanceId());
    }

    private sealed class GetMembershipViewSummaryInvokable : IInvokable
    {
        public string InterfaceName => nameof(ManagementSystemTarget);

        public string InterfaceCompatibilityFamily => "test.system-target.management";

        public int InterfaceVersion => 1;

        public string MethodName => nameof(ManagementSystemTarget.GetMembershipViewSummary);

        public ValueTask<object?> InvokeAsync(object target, CancellationToken cancellationToken)
            => ValueTask.FromResult<object?>(((ManagementSystemTarget)target).GetMembershipViewSummary());
    }

    private sealed class IdentitySystemTarget : ISystemTarget
    {
        public const string TargetType = "identity";

        private readonly string _instanceId = Guid.NewGuid().ToString("N");

        public string GetInstanceId() => _instanceId;
    }

    private sealed class ThrowingLocator : IGrainLocator
    {
        public int LocateCallCount { get; private set; }

        public GrainAddress Locate(GrainId grainId, GrainInterfaceVersionDescriptor? requestedInterface = null)
        {
            LocateCallCount++;
            throw new InvalidOperationException($"Locator should not be used for '{grainId}'.");
        }

        public void Invalidate(GrainId grainId)
        {
        }
    }

    private sealed class ThrowingTransport : IMessageTransport
    {
        public ValueTask SendAsync(InvocationMessage message, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new InvalidOperationException("System target runtime test should stay local."));
    }

    private sealed class ThrowingPlacementPolicy : IPlacementPolicy
    {
        public string SelectInitialOwner(
            GrainId grainId,
            IClusterMembershipView membershipView,
            PlacementLoadSnapshot loadSnapshot,
            GrainTypePlacementHint placementHint)
            => throw new InvalidOperationException($"Placement should not be evaluated for system target '{grainId}'.");
    }

    private sealed class StaticLoadProvider : IPlacementLoadProvider
    {
        public PlacementLoadSnapshot GetSnapshot() => new([]);
    }

    private sealed class RuntimeHarness : IAsyncDisposable
    {
        public RuntimeHarness(InProcessRuntime runtime, ThrowingLocator locator)
        {
            Runtime = runtime;
            Locator = locator;
        }

        public InProcessRuntime Runtime { get; }

        public ThrowingLocator Locator { get; }

        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }
}
