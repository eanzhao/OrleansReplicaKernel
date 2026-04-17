using OrleansReplicaKernel.Demo;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Scheduling;
using OrleansReplicaKernel.Security;
using OrleansReplicaKernel.Serialization;

namespace OrleansReplicaKernel.Tests.Runtime;

public sealed class CancellationPropagationTests
{
    private const string NodeName = "test-node";

    [Fact]
    public async Task TimeToLive_CreatesLinkedToken_ThatCancelsGrainMethod()
    {
        var grainId = new GrainId("cancel-test", "ttl");
        var grain = new CancellationObservingGrain();

        await using var activation = new ActivationEntry(
            grainId,
            grain,
            ownerVersion: 0,
            GrainTypeSchedulingPolicy.Default,
            TimeProvider.System);

        await InvokeAsync(activation, grainId, new WaitForCancellationInvokable(),
            timeToLive: TimeSpan.FromMilliseconds(100));

        Assert.True(grain.WasCancelled);
    }

    [Fact]
    public async Task TimeToLive_PropagatesThroughFilterPipeline()
    {
        var grainId = new GrainId("cancel-test", "filter-ttl");
        var grain = new CancellationObservingGrain();
        var filter = new TokenCapturingFilter();

        await using var activation = new ActivationEntry(
            grainId,
            grain,
            ownerVersion: 0,
            GrainTypeSchedulingPolicy.Default,
            TimeProvider.System,
            siloFilters: [filter]);

        await InvokeAsync(activation, grainId, new WaitForCancellationInvokable(),
            timeToLive: TimeSpan.FromMilliseconds(100));

        Assert.True(grain.WasCancelled);
    }

    [Fact]
    public async Task NoTimeToLive_UsesDefaultToken()
    {
        var grainId = new GrainId("cancel-test", "no-ttl");
        var grain = new TokenCheckingGrain();

        await using var activation = new ActivationEntry(
            grainId,
            grain,
            ownerVersion: 0,
            GrainTypeSchedulingPolicy.Default,
            TimeProvider.System);

        var result = await InvokeAsync<bool>(activation, grainId, new CheckCancellableInvokable(),
            timeToLive: null);

        Assert.False(result);
    }

    [Fact]
    public async Task WithTimeToLive_TokenIsCancellable()
    {
        var grainId = new GrainId("cancel-test", "has-ttl");
        var grain = new TokenCheckingGrain();

        await using var activation = new ActivationEntry(
            grainId,
            grain,
            ownerVersion: 0,
            GrainTypeSchedulingPolicy.Default,
            TimeProvider.System);

        var result = await InvokeAsync<bool>(activation, grainId, new CheckCancellableInvokable(),
            timeToLive: TimeSpan.FromSeconds(30));

        Assert.True(result);
    }

    [Fact]
    public void TimeToLive_RoundTrips_ThroughBinarySerialization()
    {
        var serializer = new BinarySerializerBuilder()
            .AddCodecsFromAssembly(typeof(EchoGrain).Assembly)
            .Build();

        var message = new InvocationMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            "node-a",
            new GrainAddress("node-b", new GrainId("echo", "key"), OwnerVersion: 1),
            new EchoPingSlowInvokable("test", 0),
            TimeToLive: TimeSpan.FromSeconds(5));

        var bytes = serializer.Serialize(message);
        var restored = serializer.Deserialize<InvocationMessage>(bytes);

        Assert.NotNull(restored.TimeToLive);
        Assert.Equal(TimeSpan.FromSeconds(5), restored.TimeToLive.Value);
    }

    [Fact]
    public void TimeToLive_Null_RoundTrips_ThroughBinarySerialization()
    {
        var serializer = new BinarySerializerBuilder()
            .AddCodecsFromAssembly(typeof(EchoGrain).Assembly)
            .Build();

        var message = new InvocationMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            "node-a",
            new GrainAddress("node-b", new GrainId("echo", "key"), OwnerVersion: 1),
            new EchoPingSlowInvokable("test", 0));

        var bytes = serializer.Serialize(message);
        var restored = serializer.Deserialize<InvocationMessage>(bytes);

        Assert.Null(restored.TimeToLive);
    }

    [Fact]
    public void RequestTimeout_FlowsThroughContext()
    {
        Assert.Null(ActivationExecutionContext.CurrentRequestTimeout);

        ActivationExecutionContext.CurrentRequestTimeout = TimeSpan.FromSeconds(10);
        Assert.Equal(TimeSpan.FromSeconds(10), ActivationExecutionContext.CurrentRequestTimeout);

        ActivationExecutionContext.CurrentRequestTimeout = null;
        Assert.Null(ActivationExecutionContext.CurrentRequestTimeout);
    }

    private static async ValueTask InvokeAsync(
        ActivationEntry activation,
        GrainId grainId,
        IInvokable invokable,
        TimeSpan? timeToLive)
        => await activation.InvokeAsync(
            new InvocationMessage(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                DateTimeOffset.UtcNow,
                NodeName,
                new GrainAddress(NodeName, grainId, OwnerVersion: 0),
                invokable,
                InvocationSourceKind.ClusterNode,
                InvocationIdentity.CreateLocal(NodeName, InvocationSourceKind.ClusterNode),
                TimeToLive: timeToLive),
            new StubRuntime(),
            CancellationToken.None);

    private static async ValueTask<TResult> InvokeAsync<TResult>(
        ActivationEntry activation,
        GrainId grainId,
        IInvokable invokable,
        TimeSpan? timeToLive)
        => (TResult)(await activation.InvokeAsync(
            new InvocationMessage(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                DateTimeOffset.UtcNow,
                NodeName,
                new GrainAddress(NodeName, grainId, OwnerVersion: 0),
                invokable,
                InvocationSourceKind.ClusterNode,
                InvocationIdentity.CreateLocal(NodeName, InvocationSourceKind.ClusterNode),
                TimeToLive: timeToLive),
            new StubRuntime(),
            CancellationToken.None))!;

    private sealed class CancellationObservingGrain
    {
        public bool WasCancelled { get; private set; }

        public async ValueTask WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
            }
        }
    }

    private sealed class TokenCheckingGrain
    {
        public ValueTask<bool> IsCancellableAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(cancellationToken.CanBeCanceled);
    }

    private sealed class WaitForCancellationInvokable : IInvokable
    {
        public string InterfaceName => "CancellationTest";
        public string InterfaceCompatibilityFamily => "CancellationTest";
        public int InterfaceVersion => 1;
        public string MethodName => "WaitForCancellationAsync";

        public async ValueTask<object?> InvokeAsync(object target, CancellationToken cancellationToken)
        {
            await ((CancellationObservingGrain)target).WaitForCancellationAsync(cancellationToken);
            return null;
        }
    }

    private sealed class CheckCancellableInvokable : IInvokable
    {
        public string InterfaceName => "CancellationTest";
        public string InterfaceCompatibilityFamily => "CancellationTest";
        public int InterfaceVersion => 1;
        public string MethodName => "IsCancellableAsync";

        public async ValueTask<object?> InvokeAsync(object target, CancellationToken cancellationToken)
            => await ((TokenCheckingGrain)target).IsCancellableAsync(cancellationToken);
    }

    private sealed class TokenCapturingFilter : IIncomingGrainCallFilter
    {
        public async Task InvokeAsync(IIncomingGrainCallContext context)
        {
            await context.InvokeAsync();
        }
    }

    private sealed class StubRuntime : IInvocationRuntime
    {
        public ValueTask<TResult> InvokeAsync<TResult>(
            GrainId grainId,
            IInvokable invokable,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
