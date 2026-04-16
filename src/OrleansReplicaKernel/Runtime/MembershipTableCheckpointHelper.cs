namespace OrleansReplicaKernel.Runtime;

internal static class MembershipTableCheckpointHelper
{
    public static ClusterMembershipCheckpoint Clone(ClusterMembershipCheckpoint checkpoint)
        => new(
            checkpoint.CurrentEpoch,
            checkpoint.Members
                .OrderBy(item => item.NodeName, StringComparer.Ordinal)
                .ToArray(),
            checkpoint.ViewChanges
                .OrderBy(item => item.Epoch)
                .ToArray());

    public static bool IsEmpty(ClusterMembershipCheckpoint checkpoint)
        => checkpoint.CurrentEpoch == 0
            && checkpoint.Members.Count == 0
            && checkpoint.ViewChanges.Count == 0;
}
