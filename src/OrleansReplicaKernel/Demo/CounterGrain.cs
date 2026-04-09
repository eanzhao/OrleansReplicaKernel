using OrleansReplicaKernel.App;

namespace OrleansReplicaKernel.Demo;

public sealed partial class CounterGrain : ICounterGrain
{
    private int _total;

    public Task<int> AddAsync(int delta, CancellationToken cancellationToken = default)
    {
        _total += delta;
        TraceLog.Write("grain", $"CounterGrain handle AddAsync({delta}) total={_total}");
        return Task.FromResult(_total);
    }
}
