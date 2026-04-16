using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Demo;

namespace OrleansReplicaKernel.Benchmarks;

public class GrainInvocationBenchmarks
{
    private OrleansReplicaKernelHost _host = null!;
    private IEchoGrain _localGrain = null!;
    private IEchoGrain _remoteGrain = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _host = new OrleansReplicaKernelBuilder()
            .WithLoggerFactory(NullLoggerFactory.Instance)
            .AddGeneratedGrainImplementationsFromAssembly(typeof(EchoGrain).Assembly)
            .AddGeneratedGrainReferencesFromAssembly(typeof(EchoGrainReference).Assembly)
            .Build("bench-node-1", "bench-node-2");

        _host.SetOwnerAsync<IEchoGrain>("benchmark-local", "bench-node-1").GetAwaiter().GetResult();
        _host.SetOwnerAsync<IEchoGrain>("benchmark-remote", "bench-node-2").GetAwaiter().GetResult();

        _localGrain = _host.GetGrain<IEchoGrain>("benchmark-local");
        _remoteGrain = _host.GetGrain<IEchoGrain>("benchmark-remote");

        _ = _localGrain.PingAsync("warmup-local").GetAwaiter().GetResult();
        _ = _remoteGrain.PingAsync("warmup-remote").GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Benchmark]
    public Task<string> LocalGrainCall()
    {
        return _localGrain.PingAsync("benchmark-local");
    }

    [Benchmark]
    public Task<string> RemoteGrainCall()
    {
        return _remoteGrain.PingAsync("benchmark-remote");
    }
}
