namespace OrleansReplicaKernel.Runtime;

public interface IMembershipTable
{
    ValueTask<MembershipTableSnapshot> ReadAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> RegisterAsync(
        MembershipTableWriteRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<bool> UpdateAsync(
        MembershipTableWriteRequest request,
        CancellationToken cancellationToken = default);

    ValueTask CleanupAsync(CancellationToken cancellationToken = default);
}

public sealed record MembershipTableSnapshot(
    long Version,
    ClusterMembershipCheckpoint Checkpoint);

public sealed record MembershipTableWriteRequest(
    long ExpectedVersion,
    ClusterMembershipCheckpoint Checkpoint);
