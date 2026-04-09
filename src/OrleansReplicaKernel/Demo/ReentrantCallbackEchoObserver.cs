using OrleansReplicaKernel.App;

namespace OrleansReplicaKernel.Demo;

public sealed class ReentrantCallbackEchoObserver : IEchoObserver
{
    private readonly object _lock = new();
    private readonly IEchoGrain _target;
    private readonly List<string> _observedValues = [];
    private readonly List<string> _nestedResults = [];

    public ReentrantCallbackEchoObserver(IEchoGrain target)
    {
        _target = target;
    }

    public async Task OnEchoAsync(string value, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _observedValues.Add(value);
        }

        TraceLog.Write("observer", $"ReentrantCallbackEchoObserver receive \"{value}\"");
        var nestedResult = await _target.PingAsync("callback-reenter", cancellationToken);

        lock (_lock)
        {
            _nestedResults.Add(nestedResult);
        }

        TraceLog.Write("observer", $"ReentrantCallbackEchoObserver nested result \"{nestedResult}\"");
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_lock)
        {
            return _observedValues.ToArray();
        }
    }

    public IReadOnlyList<string> NestedResults()
    {
        lock (_lock)
        {
            return _nestedResults.ToArray();
        }
    }

    public string DescribeObserved()
        => string.Join(", ", Snapshot());

    public string DescribeNestedResults()
        => string.Join(", ", NestedResults());
}
