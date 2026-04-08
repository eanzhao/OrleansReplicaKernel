namespace OrleansReplicaKernel.Runtime;

public interface IClusterMembershipView
{
    string ObserverNodeName { get; }

    long CurrentEpoch { get; }

    MembershipGossipTickResult ApplyGossip(IReadOnlyList<MembershipViewChange> changes);

    MembershipGossipTickResult RunStabilizationTick();

    NodeHealthStatus GetHealth(string nodeName);

    bool IsHealthy(string nodeName);

    IReadOnlyList<string> GetHealthyMembers();

    IReadOnlyList<ObservedClusterMemberRecord> GetMembers();
}
