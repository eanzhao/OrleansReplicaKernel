using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Demo;

namespace OrleansReplicaKernel.Tests.Runtime;

public sealed class InvocationResponseSemanticsTests
{
    [Fact]
    public async Task CallerTimeout_DiscardsLateResponse_ButExecutionStillCompletes()
    {
        await using var host = CreateHost();
        var grain = host.GetGrain<IEchoGrain>("late-response");

        var seed = await grain.PingAsync("seed");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => grain.PingSlowAsync("slow", 150, timeout.Token));

        await Task.Delay(TimeSpan.FromMilliseconds(200));

        var afterTimeout = await grain.PingAsync("after-timeout");

        Assert.Equal("echo:seed:count=1", seed);
        Assert.Equal("echo:after-timeout:count=3", afterTimeout);
    }

    [Fact]
    public async Task DroppedResponse_RetriesSameRequestId_WithoutReexecutingGrainMethod()
    {
        await using var host = CreateHost("dev-node-1", "dev-node-2");
        var grain = host.GetGrain<IEchoGrain>("dedup");

        await host.SetOwnerAsync<IEchoGrain>("dedup", "dev-node-2");

        var seed = await grain.PingAsync("seed");

        host.DropNextResponse("dev-node-2", "test drop after execution");
        var afterDrop = await grain.PingAsync("after-drop");

        Assert.Equal("echo:seed:count=1", seed);
        Assert.Equal("echo:after-drop:count=2", afterDrop);
    }

    private static OrleansReplicaKernelHost CreateHost(params string[] peerNodeNames)
        => new OrleansReplicaKernelBuilder()
            .AddGrain<IEchoGrain, EchoGrain>(
                grainType: "echo",
                grainFactory: static () => new EchoGrain(),
                referenceFactory: static (runtime, grainId) => new EchoGrainReference(runtime, grainId))
            .AddGrain<ICounterGrain, CounterGrain>(
                grainType: "counter",
                grainFactory: static () => new CounterGrain(),
                referenceFactory: static (runtime, grainId) => new CounterGrainReference(runtime, grainId))
            .Build("dev-node-1", peerNodeNames);
}
