namespace OrleansReplicaKernel.Runtime;

public readonly record struct MembershipGossipDelivery(
    string ObserverNodeName,
    long FromExclusiveEpoch,
    long ToInclusiveEpoch,
    int ConsumedChanges,
    int StabilizedNodes,
    string Mode,
    int TickNumber);
