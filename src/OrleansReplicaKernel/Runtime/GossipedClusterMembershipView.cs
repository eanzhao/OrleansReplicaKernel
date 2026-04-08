using OrleansReplicaKernel.App;

namespace OrleansReplicaKernel.Runtime;

public sealed class GossipedClusterMembershipView : IClusterMembershipView
{
    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _stabilizationWindow;
    private readonly Dictionary<string, ObservedClusterMemberRecord> _members = new(StringComparer.Ordinal);
    private long _lastConsumedEpoch;

    public GossipedClusterMembershipView(
        string observerNodeName,
        TimeSpan stabilizationWindow,
        TimeProvider? timeProvider = null)
    {
        if (stabilizationWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stabilizationWindow),
                "Membership stabilization window must be non-negative.");
        }

        ObserverNodeName = observerNodeName;
        _stabilizationWindow = stabilizationWindow;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private GossipedClusterMembershipView(
        MembershipViewCheckpoint checkpoint,
        TimeSpan stabilizationWindow,
        TimeProvider? timeProvider = null)
        : this(checkpoint.ObserverNodeName, stabilizationWindow, timeProvider)
    {
        _lastConsumedEpoch = checkpoint.CurrentEpoch;
        foreach (var member in checkpoint.Members.OrderBy(item => item.NodeName, StringComparer.Ordinal))
        {
            _members.Add(member.NodeName, member);
        }
    }

    public string ObserverNodeName { get; }

    public long CurrentEpoch
    {
        get
        {
            lock (_lock)
            {
                return _lastConsumedEpoch;
            }
        }
    }

    public MembershipGossipTickResult ApplyGossip(IReadOnlyList<MembershipViewChange> changes)
    {
        var consumed = 0;

        lock (_lock)
        {
            foreach (var change in changes.OrderBy(item => item.Epoch))
            {
                if (change.Epoch <= _lastConsumedEpoch)
                {
                    continue;
                }

                consumed++;
                _lastConsumedEpoch = change.Epoch;

                if (!_members.TryGetValue(change.NodeName, out var existing))
                {
                    var created = new ObservedClusterMemberRecord(
                        change.NodeName,
                        StableStatus: change.CurrentStatus,
                        ObservedStatus: change.CurrentStatus,
                        LastObservedEpoch: change.Epoch,
                        LastObservedAtUtc: change.CreatedAtUtc);
                    _members.Add(change.NodeName, created);
                    TraceLog.Write(
                        "gossip",
                        $"{ObserverNodeName} learn initial view epoch={change.Epoch} node={change.NodeName} stable={change.CurrentStatus}");
                    continue;
                }

                var updated = existing with
                {
                    ObservedStatus = change.CurrentStatus,
                    LastObservedEpoch = change.Epoch,
                    LastObservedAtUtc = change.CreatedAtUtc,
                };
                _members[change.NodeName] = updated;
                TraceLog.Write(
                    "gossip",
                    $"{ObserverNodeName} consume view change epoch={change.Epoch} node={change.NodeName} observed={change.CurrentStatus} reason={change.Reason}");
            }

            var stabilized = StabilizeLocked(_timeProvider.GetUtcNow());
            return new MembershipGossipTickResult(consumed, stabilized);
        }
    }

    public MembershipGossipTickResult RunStabilizationTick()
    {
        lock (_lock)
        {
            var stabilized = StabilizeLocked(_timeProvider.GetUtcNow());
            return new MembershipGossipTickResult(0, stabilized);
        }
    }

    public NodeHealthStatus GetHealth(string nodeName)
    {
        lock (_lock)
        {
            return _members.TryGetValue(nodeName, out var record)
                ? record.StableStatus
                : NodeHealthStatus.Unhealthy;
        }
    }

    public bool IsHealthy(string nodeName) => GetHealth(nodeName) == NodeHealthStatus.Healthy;

    public IReadOnlyList<string> GetHealthyMembers()
    {
        lock (_lock)
        {
            return _members
                .Values
                .Where(item => item.StableStatus == NodeHealthStatus.Healthy)
                .Select(item => item.NodeName)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IReadOnlyList<ObservedClusterMemberRecord> GetMembers()
    {
        lock (_lock)
        {
            return _members.Values
                .OrderBy(item => item.NodeName, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public MembershipViewCheckpoint ExportCheckpoint()
    {
        lock (_lock)
        {
            return new MembershipViewCheckpoint(
                ObserverNodeName,
                _lastConsumedEpoch,
                _members.Values
                    .OrderBy(item => item.NodeName, StringComparer.Ordinal)
                    .ToArray());
        }
    }

    public static GossipedClusterMembershipView Restore(
        MembershipViewCheckpoint checkpoint,
        TimeSpan stabilizationWindow,
        TimeProvider? timeProvider = null)
        => new(checkpoint, stabilizationWindow, timeProvider);

    private int StabilizeLocked(DateTimeOffset utcNow)
    {
        var stabilized = 0;

        foreach (var pair in _members.ToArray())
        {
            var record = pair.Value;
            if (record.StableStatus == record.ObservedStatus)
            {
                continue;
            }

            if (utcNow - record.LastObservedAtUtc < _stabilizationWindow)
            {
                continue;
            }

            _members[pair.Key] = record with { StableStatus = record.ObservedStatus };
            stabilized++;
            TraceLog.Write(
                "stabilization",
                $"{ObserverNodeName} stabilize {pair.Key} -> {record.ObservedStatus} after {_stabilizationWindow}");
        }

        return stabilized;
    }
}
