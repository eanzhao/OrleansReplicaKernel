namespace OrleansReplicaKernel.Runtime;

public interface IMembershipGossiper
{
    IReadOnlyList<MembershipGossipDelivery> Gossip();

    MembershipDisseminationCheckpoint ExportCheckpoint();
}
