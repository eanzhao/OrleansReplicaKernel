using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Scheduling;
using OrleansReplicaKernel.Security;

namespace OrleansReplicaKernel.Tests.Runtime;

public sealed class GrainCallFilterTests
{
    private const string NodeName = "test-node";

    [Fact]
    public async Task SiloFilter_IsInvoked_BeforeGrainMethod()
    {
        var log = new List<string>();
        var grainId = new GrainId("filter-test", "silo-filter");
        var filter = new LoggingFilter(log, "silo");

        await using var activation = new ActivationEntry(
            grainId,
            new SimpleGrain(log),
            ownerVersion: 0,
            GrainTypeSchedulingPolicy.Default,
            TimeProvider.System,
            siloFilters: [filter]);

        var result = await InvokeAsync<string>(activation, grainId, new EchoInvokable("hello"));

        Assert.Equal("hello", result);
        Assert.Equal(["silo:before:EchoAsync", "grain:EchoAsync", "silo:after:EchoAsync"], log);
    }

    [Fact]
    public async Task GrainLevelFilter_IsInvoked_WhenGrainImplementsInterface()
    {
        var log = new List<string>();
        var grainId = new GrainId("filter-test", "grain-filter");

        await using var activation = new ActivationEntry(
            grainId,
            new FilteringGrain(log),
            ownerVersion: 0,
            GrainTypeSchedulingPolicy.Default,
            TimeProvider.System);

        var result = await InvokeAsync<string>(activation, grainId, new EchoInvokable("world"));

        Assert.Equal("world", result);
        Assert.Equal(["grain-filter:before:EchoAsync", "grain:EchoAsync", "grain-filter:after:EchoAsync"], log);
    }

    [Fact]
    public async Task SiloAndGrainFilters_ExecuteInOrder_SiloThenGrain()
    {
        var log = new List<string>();
        var grainId = new GrainId("filter-test", "both");
        var siloFilter = new LoggingFilter(log, "silo");

        await using var activation = new ActivationEntry(
            grainId,
            new FilteringGrain(log),
            ownerVersion: 0,
            GrainTypeSchedulingPolicy.Default,
            TimeProvider.System,
            siloFilters: [siloFilter]);

        var result = await InvokeAsync<string>(activation, grainId, new EchoInvokable("test"));

        Assert.Equal("test", result);
        Assert.Equal([
            "silo:before:EchoAsync",
            "grain-filter:before:EchoAsync",
            "grain:EchoAsync",
            "grain-filter:after:EchoAsync",
            "silo:after:EchoAsync"
        ], log);
    }

    [Fact]
    public async Task MultipleFilters_ChainCorrectly()
    {
        var log = new List<string>();
        var grainId = new GrainId("filter-test", "multi");
        var filter1 = new LoggingFilter(log, "auth");
        var filter2 = new LoggingFilter(log, "metrics");

        await using var activation = new ActivationEntry(
            grainId,
            new SimpleGrain(log),
            ownerVersion: 0,
            GrainTypeSchedulingPolicy.Default,
            TimeProvider.System,
            siloFilters: [filter1, filter2]);

        await InvokeAsync<string>(activation, grainId, new EchoInvokable("x"));

        Assert.Equal([
            "auth:before:EchoAsync",
            "metrics:before:EchoAsync",
            "grain:EchoAsync",
            "metrics:after:EchoAsync",
            "auth:after:EchoAsync"
        ], log);
    }

    [Fact]
    public async Task Filter_CanShortCircuit_ByNotCallingNext()
    {
        var grainId = new GrainId("filter-test", "short-circuit");
        var filter = new ShortCircuitFilter("blocked");

        await using var activation = new ActivationEntry(
            grainId,
            new SimpleGrain(new List<string>()),
            ownerVersion: 0,
            GrainTypeSchedulingPolicy.Default,
            TimeProvider.System,
            siloFilters: [filter]);

        var result = await InvokeAsync<string>(activation, grainId, new EchoInvokable("hello"));

        Assert.Equal("blocked", result);
    }

    [Fact]
    public async Task NoFilters_InvokesDirectly()
    {
        var log = new List<string>();
        var grainId = new GrainId("filter-test", "no-filter");

        await using var activation = new ActivationEntry(
            grainId,
            new SimpleGrain(log),
            ownerVersion: 0,
            GrainTypeSchedulingPolicy.Default,
            TimeProvider.System);

        var result = await InvokeAsync<string>(activation, grainId, new EchoInvokable("direct"));

        Assert.Equal("direct", result);
        Assert.Equal(["grain:EchoAsync"], log);
    }

    private static async ValueTask<TResult> InvokeAsync<TResult>(
        ActivationEntry activation,
        GrainId grainId,
        IInvokable invokable)
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
                InvocationIdentity.CreateLocal(NodeName, InvocationSourceKind.ClusterNode)),
            new StubInvocationRuntime(),
            CancellationToken.None))!;

    private interface ITestGrain
    {
        ValueTask<string> EchoAsync(string input, CancellationToken cancellationToken = default);
    }

    private sealed class SimpleGrain : ITestGrain
    {
        private readonly List<string> _log;

        public SimpleGrain(List<string> log) => _log = log;

        public ValueTask<string> EchoAsync(string input, CancellationToken cancellationToken = default)
        {
            _log.Add("grain:EchoAsync");
            return ValueTask.FromResult(input);
        }
    }

    private sealed class FilteringGrain : ITestGrain, IIncomingGrainCallFilter
    {
        private readonly List<string> _log;

        public FilteringGrain(List<string> log) => _log = log;

        public ValueTask<string> EchoAsync(string input, CancellationToken cancellationToken = default)
        {
            _log.Add("grain:EchoAsync");
            return ValueTask.FromResult(input);
        }

        public async Task InvokeAsync(IIncomingGrainCallContext context)
        {
            _log.Add($"grain-filter:before:{context.Request.MethodName}");
            await context.InvokeAsync();
            _log.Add($"grain-filter:after:{context.Request.MethodName}");
        }
    }

    private sealed class LoggingFilter : IIncomingGrainCallFilter
    {
        private readonly List<string> _log;
        private readonly string _name;

        public LoggingFilter(List<string> log, string name)
        {
            _log = log;
            _name = name;
        }

        public async Task InvokeAsync(IIncomingGrainCallContext context)
        {
            _log.Add($"{_name}:before:{context.Request.MethodName}");
            await context.InvokeAsync();
            _log.Add($"{_name}:after:{context.Request.MethodName}");
        }
    }

    private sealed class ShortCircuitFilter : IIncomingGrainCallFilter
    {
        private readonly string _result;

        public ShortCircuitFilter(string result) => _result = result;

        public Task InvokeAsync(IIncomingGrainCallContext context)
        {
            context.Result = _result;
            return Task.CompletedTask;
        }
    }

    private sealed class EchoInvokable : IInvokable
    {
        private readonly string _input;

        public EchoInvokable(string input) => _input = input;

        public string InterfaceName => nameof(ITestGrain);

        public string InterfaceCompatibilityFamily => typeof(ITestGrain).FullName!;

        public int InterfaceVersion => 1;

        public string MethodName => nameof(ITestGrain.EchoAsync);

        public async ValueTask<object?> InvokeAsync(object target, CancellationToken cancellationToken)
            => await ((ITestGrain)target).EchoAsync(_input, cancellationToken);
    }

    private sealed class StubInvocationRuntime : IInvocationRuntime
    {
        public ValueTask<TResult> InvokeAsync<TResult>(
            GrainId grainId,
            IInvokable invokable,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
