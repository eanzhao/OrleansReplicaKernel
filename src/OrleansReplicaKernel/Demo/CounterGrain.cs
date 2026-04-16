using OrleansReplicaKernel.App;
using OrleansReplicaKernel.CodeGeneration;

namespace OrleansReplicaKernel.Demo;

[CollectionAgeLimit(100)]
[PreferLocalPlacement]
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
