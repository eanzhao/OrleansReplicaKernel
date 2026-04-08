namespace OrleansReplicaKernel.Runtime;

public readonly record struct MembershipObserverCursor(
    string ObserverNodeName,
    long LastDeliveredEpoch);
