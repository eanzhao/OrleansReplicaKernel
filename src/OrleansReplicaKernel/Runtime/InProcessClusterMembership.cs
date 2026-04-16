using OrleansReplicaKernel.App;

namespace OrleansReplicaKernel.Runtime;

public sealed class InProcessClusterMembership : IClusterMembership
{
    private readonly IMembershipTable _membershipTable;
    private readonly TimeProvider _timeProvider;

    public InProcessClusterMembership(TimeProvider? timeProvider = null)
        : this(new InMemoryMembershipTable(), timeProvider)
    {
    }

    public InProcessClusterMembership(IMembershipTable membershipTable, TimeProvider? timeProvider = null)
    {
        _membershipTable = membershipTable ?? throw new ArgumentNullException(nameof(membershipTable));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public long CurrentEpoch => ReadCheckpoint().CurrentEpoch;

    public void Register(string nodeName)
    {
        var utcNow = _timeProvider.GetUtcNow();
        while (true)
        {
            var snapshot = ReadTableSnapshot();
            if (snapshot.Checkpoint.Members.Any(item => string.Equals(item.NodeName, nodeName, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"Node '{nodeName}' is already part of membership.");
            }

            var updatedEpoch = snapshot.Checkpoint.CurrentEpoch + 1;
            var viewChange = new MembershipViewChange(
                updatedEpoch,
                nodeName,
                PreviousStatus: null,
                CurrentStatus: NodeHealthStatus.Healthy,
                Reason: "register",
                CreatedAtUtc: utcNow);

            var updatedCheckpoint = new ClusterMembershipCheckpoint(
                updatedEpoch,
                snapshot.Checkpoint.Members
                    .Append(new ClusterMemberRecord(nodeName, NodeHealthStatus.Healthy))
                    .OrderBy(item => item.NodeName, StringComparer.Ordinal)
                    .ToArray(),
                snapshot.Checkpoint.ViewChanges
                    .Append(viewChange)
                    .OrderBy(item => item.Epoch)
                    .ToArray());

            if (!_membershipTable.RegisterAsync(
                    new MembershipTableWriteRequest(snapshot.Version, updatedCheckpoint))
                .GetAwaiter()
                .GetResult())
            {
                continue;
            }

            TraceLog.Write(
                "membership",
                $"view change epoch={viewChange.Epoch} node={nodeName} <none> -> {NodeHealthStatus.Healthy} reason={viewChange.Reason}");
            return;
        }
    }

    public bool IsMember(string nodeName)
        => ReadCheckpoint().Members.Any(item => string.Equals(item.NodeName, nodeName, StringComparison.Ordinal));

    public bool IsHealthy(string nodeName)
        => GetHealth(nodeName) == NodeHealthStatus.Healthy;

    public NodeHealthStatus GetHealth(string nodeName)
    {
        var record = ReadCheckpoint().Members
            .FirstOrDefault(item => string.Equals(item.NodeName, nodeName, StringComparison.Ordinal));
        return string.IsNullOrWhiteSpace(record.NodeName)
            ? NodeHealthStatus.Unhealthy
            : record.HealthStatus;
    }

    public void SetHealth(string nodeName, NodeHealthStatus status, string reason)
    {
        var utcNow = _timeProvider.GetUtcNow();
        while (true)
        {
            var snapshot = ReadTableSnapshot();
            var record = snapshot.Checkpoint.Members
                .FirstOrDefault(item => string.Equals(item.NodeName, nodeName, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(record.NodeName))
            {
                throw new InvalidOperationException($"Node '{nodeName}' is not part of membership.");
            }

            if (record.HealthStatus == status)
            {
                return;
            }

            var updatedEpoch = snapshot.Checkpoint.CurrentEpoch + 1;
            var viewChange = new MembershipViewChange(
                updatedEpoch,
                nodeName,
                record.HealthStatus,
                status,
                reason,
                utcNow);

            var updatedCheckpoint = new ClusterMembershipCheckpoint(
                updatedEpoch,
                snapshot.Checkpoint.Members
                    .Select(item => string.Equals(item.NodeName, nodeName, StringComparison.Ordinal)
                        ? item with { HealthStatus = status }
                        : item)
                    .OrderBy(item => item.NodeName, StringComparer.Ordinal)
                    .ToArray(),
                snapshot.Checkpoint.ViewChanges
                    .Append(viewChange)
                    .OrderBy(item => item.Epoch)
                    .ToArray());

            if (!_membershipTable.UpdateAsync(
                    new MembershipTableWriteRequest(snapshot.Version, updatedCheckpoint))
                .GetAwaiter()
                .GetResult())
            {
                continue;
            }

            TraceLog.Write(
                "membership",
                $"view change epoch={viewChange.Epoch} node={nodeName} {viewChange.PreviousStatus} -> {viewChange.CurrentStatus} reason={viewChange.Reason}");
            return;
        }
    }

    public IReadOnlyList<ClusterMemberRecord> GetMembers()
        => ReadCheckpoint().Members
            .OrderBy(item => item.NodeName, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<string> GetHealthyMembers()
        => ReadCheckpoint().Members
            .Where(item => item.HealthStatus == NodeHealthStatus.Healthy)
            .Select(item => item.NodeName)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<MembershipViewChange> GetViewChanges()
        => ReadCheckpoint().ViewChanges
            .OrderBy(item => item.Epoch)
            .ToArray();

    public IReadOnlyList<MembershipViewChange> GetViewChangesSince(long epochExclusive)
        => ReadCheckpoint().ViewChanges
            .Where(item => item.Epoch > epochExclusive)
            .OrderBy(item => item.Epoch)
            .ToArray();

    public ClusterMembershipCheckpoint ExportCheckpoint()
        => MembershipTableCheckpointHelper.Clone(ReadCheckpoint());

    public static InProcessClusterMembership Restore(
        ClusterMembershipCheckpoint checkpoint,
        TimeProvider? timeProvider = null)
        => new(new InMemoryMembershipTable(checkpoint), timeProvider);

    private ClusterMembershipCheckpoint ReadCheckpoint() => ReadTableSnapshot().Checkpoint;

    private MembershipTableSnapshot ReadTableSnapshot()
        => _membershipTable.ReadAsync().GetAwaiter().GetResult();
}
