using System.Threading.Channels;
using OrleansReplicaKernel.App;

namespace OrleansReplicaKernel.Scheduling;

public sealed class ActivationScheduler : IAsyncDisposable
{
    private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>();
    private readonly Task _worker;

    public ActivationScheduler(string activationName)
    {
        ActivationName = activationName;
        _worker = Task.Run(RunAsync);
    }

    public string ActivationName { get; }

    public async ValueTask<object?> EnqueueAsync(
        string operationName,
        Func<CancellationToken, ValueTask<object?>> callback,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await _queue.Writer.WriteAsync(new WorkItem(operationName, callback, completion), cancellationToken);
        TraceLog.Write("scheduler", $"enqueue {operationName} on {ActivationName}");

        return await completion.Task.WaitAsync(cancellationToken);
    }

    private async Task RunAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync())
        {
            try
            {
                TraceLog.Write("scheduler", $"begin turn {item.OperationName} on {ActivationName}");
                var result = await item.Callback(CancellationToken.None);
                item.Completion.TrySetResult(result);
                TraceLog.Write("scheduler", $"end turn {item.OperationName} on {ActivationName}");
            }
            catch (Exception exception)
            {
                item.Completion.TrySetException(exception);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker;
    }

    private sealed record WorkItem(
        string OperationName,
        Func<CancellationToken, ValueTask<object?>> Callback,
        TaskCompletionSource<object?> Completion);
}
