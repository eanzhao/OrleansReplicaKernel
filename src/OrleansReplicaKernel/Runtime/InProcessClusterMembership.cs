using OrleansReplicaKernel.App;

namespace OrleansReplicaKernel.Runtime;

public sealed class InProcessClusterMembership : IClusterMembership
{
    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, ClusterMemberRecord> _members = new(StringComparer.Ordinal);
    private readonly List<MembershipViewChange> _viewChanges = [];
    private long _currentEpoch;

    public InProcessClusterMembership(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private InProcessClusterMembership(ClusterMembershipCheckpoint checkpoint, TimeProvider? timeProvider)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _currentEpoch = checkpoint.CurrentEpoch;
        foreach (var member in checkpoint.Members)
        {
            _members.Add(member.NodeName, member);
        }

        _viewChanges.AddRange(checkpoint.ViewChanges.OrderBy(item => item.Epoch));
    }

    public long CurrentEpoch
    {
        get
        {
            lock (_lock)
            {
                return _currentEpoch;
            }
        }
    }

    public void Register(string nodeName)
    {
        var utcNow = _timeProvider.GetUtcNow();
        MembershipViewChange viewChange;
        lock (_lock)
        {
            _members.Add(nodeName, new ClusterMemberRecord(nodeName, NodeHealthStatus.Healthy));
            _currentEpoch++;
            viewChange = new MembershipViewChange(
                _currentEpoch,
                nodeName,
                PreviousStatus: null,
                CurrentStatus: NodeHealthStatus.Healthy,
                Reason: "register",
                CreatedAtUtc: utcNow);
            _viewChanges.Add(viewChange);
        }

        TraceLog.Write(
            "membership",
            $"view change epoch={viewChange.Epoch} node={nodeName} <none> -> {NodeHealthStatus.Healthy} reason={viewChange.Reason}");
    }

    public bool IsMember(string nodeName)
    {
        lock (_lock)
        {
            return _members.ContainsKey(nodeName);
        }
    }

    public bool IsHealthy(string nodeName)
        => GetHealth(nodeName) == NodeHealthStatus.Healthy;

    public NodeHealthStatus GetHealth(string nodeName)
    {
        lock (_lock)
        {
            return _members.TryGetValue(nodeName, out var record)
                ? record.HealthStatus
                : NodeHealthStatus.Unhealthy;
        }
    }

    public void SetHealth(string nodeName, NodeHealthStatus status, string reason)
    {
        var utcNow = _timeProvider.GetUtcNow();
        MembershipViewChange? viewChange = null;
        lock (_lock)
        {
            if (_members.TryGetValue(nodeName, out var record))
            {
                if (record.HealthStatus == status)
                {
                    return;
                }

                var updated = record with { HealthStatus = status };
                _members[nodeName] = updated;
                _currentEpoch++;
                viewChange = new MembershipViewChange(
                    _currentEpoch,
                    nodeName,
                    record.HealthStatus,
                    status,
                    reason,
                    utcNow);
                _viewChanges.Add(viewChange);
            }
            else
            {
                throw new InvalidOperationException($"Node '{nodeName}' is not part of membership.");
            }
        }

        TraceLog.Write(
            "membership",
            $"view change epoch={viewChange!.Epoch} node={nodeName} {viewChange.PreviousStatus} -> {viewChange.CurrentStatus} reason={viewChange.Reason}");
    }

    public IReadOnlyList<ClusterMemberRecord> GetMembers()
    {
        lock (_lock)
        {
            return _members.Values.OrderBy(item => item.NodeName, StringComparer.Ordinal).ToArray();
        }
    }

    public IReadOnlyList<string> GetHealthyMembers()
    {
        lock (_lock)
        {
            return _members
                .Where(pair => pair.Value.HealthStatus == NodeHealthStatus.Healthy)
                .Select(pair => pair.Value.NodeName)
                .ToArray();
        }
    }

    public IReadOnlyList<MembershipViewChange> GetViewChanges()
    {
        lock (_lock)
        {
            return _viewChanges.ToArray();
        }
    }

    public IReadOnlyList<MembershipViewChange> GetViewChangesSince(long epochExclusive)
    {
        lock (_lock)
        {
            return _viewChanges
                .Where(item => item.Epoch > epochExclusive)
                .ToArray();
        }
    }

    public ClusterMembershipCheckpoint ExportCheckpoint()
    {
        lock (_lock)
        {
            return new ClusterMembershipCheckpoint(
                _currentEpoch,
                _members.Values
                    .OrderBy(item => item.NodeName, StringComparer.Ordinal)
                    .ToArray(),
                _viewChanges
                    .OrderBy(item => item.Epoch)
                    .ToArray());
        }
    }

    public static InProcessClusterMembership Restore(
        ClusterMembershipCheckpoint checkpoint,
        TimeProvider? timeProvider = null)
        => new(checkpoint, timeProvider);
}
