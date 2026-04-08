using OrleansReplicaKernel.App;

namespace OrleansReplicaKernel.Demo;

public sealed class RecordingEchoObserver : IEchoObserver
{
    private readonly object _lock = new();
    private readonly List<string> _values = [];

    public Task OnEchoAsync(string value, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _values.Add(value);
        }

        TraceLog.Write("observer", $"RecordingEchoObserver receive \"{value}\"");
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_lock)
        {
            return _values.ToArray();
        }
    }

    public string Describe()
        => string.Join(", ", Snapshot());
}
