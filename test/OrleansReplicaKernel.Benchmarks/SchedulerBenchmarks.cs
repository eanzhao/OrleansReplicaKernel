using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Scheduling;

namespace OrleansReplicaKernel.Benchmarks;

public class SchedulerBenchmarks
{
    private const int TurnsPerInvocation = 10_000;
    private Guid[] _requestChainIds = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        TraceLog.Configure(NullLoggerFactory.Instance);
        _requestChainIds = new Guid[TurnsPerInvocation];
        for (var i = 0; i < TurnsPerInvocation; i++)
        {
            _requestChainIds[i] = new Guid(i + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    [Benchmark(OperationsPerInvoke = TurnsPerInvocation)]
    public async Task ExclusiveTurnThroughput()
    {
        await using var scheduler = new ActivationScheduler("benchmark/exclusive");
        var turns = new Task[TurnsPerInvocation];
        for (var i = 0; i < TurnsPerInvocation; i++)
        {
            turns[i] = scheduler.EnqueueAsync(
                operationName: "exclusive",
                allowInterleaving: false,
                requestChainId: _requestChainIds[i],
                callback: CompletedTurnAsync,
                cancellationToken: CancellationToken.None).AsTask();
        }

        await Task.WhenAll(turns);
    }

    [Benchmark(OperationsPerInvoke = TurnsPerInvocation)]
    public async Task InterleavedTurnThroughput()
    {
        await using var scheduler = new ActivationScheduler("benchmark/interleaved");
        var turns = new Task[TurnsPerInvocation];
        for (var i = 0; i < TurnsPerInvocation; i++)
        {
            turns[i] = scheduler.EnqueueAsync(
                operationName: "interleaved",
                allowInterleaving: true,
                requestChainId: _requestChainIds[i],
                callback: YieldingTurnAsync,
                cancellationToken: CancellationToken.None).AsTask();
        }

        await Task.WhenAll(turns);
    }

    private static ValueTask<object?> CompletedTurnAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<object?>(null);
    }

    private static async ValueTask<object?> YieldingTurnAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        return null;
    }
}
