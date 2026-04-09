using System.Threading.Channels;
using OrleansReplicaKernel.App;

namespace OrleansReplicaKernel.Scheduling;

public sealed class ActivationScheduler : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly Queue<WorkItem> _pending = new();
    private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>();
    private readonly Task _reader;
    private readonly TaskCompletionSource<bool> _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _exclusiveTurnActive;
    private bool _inputCompleted;
    private int _activeTurnCount;

    public ActivationScheduler(string activationName)
    {
        ActivationName = activationName;
        _reader = Task.Run(ReadLoopAsync);
    }

    public string ActivationName { get; }

    public async ValueTask<object?> EnqueueAsync(
        string operationName,
        bool allowInterleaving,
        Func<CancellationToken, ValueTask<object?>> callback,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await _queue.Writer.WriteAsync(
            new WorkItem(operationName, allowInterleaving, callback, completion),
            cancellationToken);
        TraceLog.Write(
            "scheduler",
            $"enqueue {operationName} on {ActivationName} mode={(allowInterleaving ? "interleavable" : "exclusive")}");

        return await completion.Task.WaitAsync(cancellationToken);
    }

    private async Task ReadLoopAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync())
        {
            lock (_lock)
            {
                _pending.Enqueue(item);
                TryDispatchPendingUnsafe();
            }
        }

        lock (_lock)
        {
            _inputCompleted = true;
            SignalDrainedIfNeededUnsafe();
        }
    }

    private void TryDispatchPendingUnsafe()
    {
        while (_pending.Count > 0)
        {
            if (_exclusiveTurnActive)
            {
                return;
            }

            var next = _pending.Peek();
            if (!next.AllowInterleaving)
            {
                if (_activeTurnCount != 0)
                {
                    return;
                }

                _pending.Dequeue();
                _exclusiveTurnActive = true;
                _activeTurnCount++;
                Dispatch(next);
                return;
            }

            _pending.Dequeue();
            _activeTurnCount++;
            Dispatch(next);
        }
    }

    private void Dispatch(WorkItem item)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                TraceLog.Write(
                    "scheduler",
                    $"begin turn {item.OperationName} on {ActivationName} mode={(item.AllowInterleaving ? "interleavable" : "exclusive")}");
                var result = await item.Callback(CancellationToken.None);
                item.Completion.TrySetResult(result);
                TraceLog.Write(
                    "scheduler",
                    $"end turn {item.OperationName} on {ActivationName} mode={(item.AllowInterleaving ? "interleavable" : "exclusive")}");
            }
            catch (Exception exception)
            {
                item.Completion.TrySetException(exception);
            }
            finally
            {
                lock (_lock)
                {
                    _activeTurnCount--;
                    if (!item.AllowInterleaving)
                    {
                        _exclusiveTurnActive = false;
                    }

                    TryDispatchPendingUnsafe();
                    SignalDrainedIfNeededUnsafe();
                }
            }
        });
    }

    private void SignalDrainedIfNeededUnsafe()
    {
        if (_inputCompleted && _pending.Count == 0 && _activeTurnCount == 0)
        {
            _drained.TrySetResult(true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _reader;
        await _drained.Task;
    }

    private sealed record WorkItem(
        string OperationName,
        bool AllowInterleaving,
        Func<CancellationToken, ValueTask<object?>> Callback,
        TaskCompletionSource<object?> Completion);
}
