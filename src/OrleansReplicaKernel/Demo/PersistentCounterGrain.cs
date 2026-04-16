using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Storage;

namespace OrleansReplicaKernel.Demo;

public sealed partial class PersistentCounterGrain : IPersistentCounterGrain
{
    private readonly IPersistentState<PersistentCounterGrainState> _counterState;

    public PersistentCounterGrain(
        [PersistentState("counter")] IPersistentState<PersistentCounterGrainState> counterState)
    {
        _counterState = counterState;
    }

    public async Task<int> AddAsync(int delta, CancellationToken cancellationToken = default)
    {
        _counterState.State.Total += delta;
        await _counterState.WriteStateAsync(cancellationToken);
        TraceLog.Write("grain", $"PersistentCounterGrain handle AddAsync({delta}) total={_counterState.State.Total}");
        return _counterState.State.Total;
    }

    public Task<int> GetValueAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_counterState.State.Total);
}

public sealed class PersistentCounterGrainState
{
    public int Total { get; set; }
}
