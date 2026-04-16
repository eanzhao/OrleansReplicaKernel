namespace OrleansReplicaKernel.Runtime;

public sealed class InMemoryMembershipTable : IMembershipTable
{
    private readonly object _lock = new();
    private ClusterMembershipCheckpoint _checkpoint;
    private long _version;

    public InMemoryMembershipTable()
        : this(ClusterMembershipCheckpoint.Empty)
    {
    }

    public InMemoryMembershipTable(ClusterMembershipCheckpoint checkpoint)
    {
        _checkpoint = MembershipTableCheckpointHelper.Clone(checkpoint);
        _version = MembershipTableCheckpointHelper.IsEmpty(checkpoint) ? 0 : 1;
    }

    public ValueTask<MembershipTableSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            return ValueTask.FromResult(
                new MembershipTableSnapshot(
                    _version,
                    MembershipTableCheckpointHelper.Clone(_checkpoint)));
        }
    }

    public ValueTask<bool> RegisterAsync(
        MembershipTableWriteRequest request,
        CancellationToken cancellationToken = default)
        => WriteAsync(request, cancellationToken);

    public ValueTask<bool> UpdateAsync(
        MembershipTableWriteRequest request,
        CancellationToken cancellationToken = default)
        => WriteAsync(request, cancellationToken);

    public ValueTask CleanupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private ValueTask<bool> WriteAsync(
        MembershipTableWriteRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (request.ExpectedVersion != _version)
            {
                return ValueTask.FromResult(false);
            }

            _checkpoint = MembershipTableCheckpointHelper.Clone(request.Checkpoint);
            _version++;
            return ValueTask.FromResult(true);
        }
    }
}
