using System.Collections.Concurrent;
using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Scheduling;

namespace OrleansReplicaKernel.Runtime;

public sealed class SystemTargetDirectory : IAsyncDisposable
{
    private readonly string _localNodeName;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<GrainId, ActivationEntry> _entries = new();

    public SystemTargetDirectory(string localNodeName, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localNodeName);

        _localNodeName = localNodeName;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void Register(SystemTargetId id, object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance is not ISystemTarget)
        {
            throw new InvalidOperationException(
                $"System target '{id}' must implement {nameof(ISystemTarget)}.");
        }

        if (!string.Equals(id.NodeName, _localNodeName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot register system target '{id.TargetType}' for node '{id.NodeName}' in local directory '{_localNodeName}'.");
        }

        var grainId = id.ToGrainId();
        var entry = new ActivationEntry(
            grainId,
            instance,
            ownerVersion: 0,
            GrainTypeSchedulingPolicy.Default,
            _timeProvider);

        if (!_entries.TryAdd(grainId, entry))
        {
            throw new InvalidOperationException(
                $"A system target is already registered for grain id '{grainId}' on node '{_localNodeName}'.");
        }

        TraceLog.Write("system-target", $"register {grainId} on {_localNodeName}");
    }

    public bool TryGet(GrainId grainId, out ActivationEntry entry)
        => _entries.TryGetValue(grainId, out entry!);

    public async ValueTask DisposeAsync()
    {
        var entries = _entries.ToArray();
        _entries.Clear();

        foreach (var entry in entries.Select(item => item.Value))
        {
            await entry.DisposeAsync(ActivationDeactivationReason.Shutdown);
        }
    }
}
