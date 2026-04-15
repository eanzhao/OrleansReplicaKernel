using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Scheduling;

namespace OrleansReplicaKernel.Runtime;

public sealed class LocalCallbackDirectory : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<GrainId, ActivationEntry> _callbacks = new();

    public LocalCallbackDirectory(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public GrainId Register(string nodeName, string callbackType, object implementation)
    {
        var grainId = CallbackTargetIdentity.Create(callbackType, nodeName);
        var activation = new ActivationEntry(
            grainId,
            implementation,
            ownerVersion: 0,
            schedulingPolicy: GrainTypeSchedulingPolicy.Default,
            timeProvider: _timeProvider);

        lock (_lock)
        {
            _callbacks.Add(grainId, activation);
        }

        TraceLog.Write("callback", $"register {grainId} on {nodeName}");
        return grainId;
    }

    public ActivationEntry GetRequired(GrainId grainId)
    {
        lock (_lock)
        {
            if (_callbacks.TryGetValue(grainId, out var callback))
            {
                return callback;
            }
        }

        throw new InvalidOperationException($"No callback target registered for '{grainId}'.");
    }

    public async ValueTask<bool> UnregisterAsync(GrainId grainId)
    {
        ActivationEntry? callback;

        lock (_lock)
        {
            if (!_callbacks.Remove(grainId, out callback))
            {
                return false;
            }
        }

        TraceLog.Write("callback", $"unregister {grainId}");
        await callback.DisposeAsync();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        List<ActivationEntry> callbacks;

        lock (_lock)
        {
            callbacks = _callbacks.Values.ToList();
            _callbacks.Clear();
        }

        foreach (var callback in callbacks)
        {
            await callback.DisposeAsync();
        }
    }
}
