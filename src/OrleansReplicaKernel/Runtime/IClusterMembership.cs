namespace OrleansReplicaKernel.Runtime;

public interface IClusterMembership
{
    long CurrentEpoch { get; }

    void Register(string nodeName);

    bool IsMember(string nodeName);

    NodeHealthStatus GetHealth(string nodeName);

    bool IsHealthy(string nodeName);

    void SetHealth(string nodeName, NodeHealthStatus status, string reason);

    IReadOnlyList<ClusterMemberRecord> GetMembers();

    IReadOnlyList<string> GetHealthyMembers();

    IReadOnlyList<MembershipViewChange> GetViewChanges();

    IReadOnlyList<MembershipViewChange> GetViewChangesSince(long epochExclusive);

    ClusterMembershipCheckpoint ExportCheckpoint();
}
