namespace OrleansReplicaKernel.Runtime;

public readonly record struct MembershipGossipTickResult(
    int ConsumedChanges,
    int StabilizedNodes);
