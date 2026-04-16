namespace OrleansReplicaKernel.Routing;

public sealed class InMemoryGrainDirectoryTable : IGrainDirectoryTable
{
    private readonly object _lock = new();
    private GrainDirectoryCheckpoint _checkpoint;
    private long _version;

    public InMemoryGrainDirectoryTable()
        : this(GrainDirectoryCheckpoint.Empty)
    {
    }

    public InMemoryGrainDirectoryTable(GrainDirectoryCheckpoint checkpoint)
    {
        _checkpoint = GrainDirectoryCheckpointHelper.Clone(checkpoint);
        _version = GrainDirectoryCheckpointHelper.IsEmpty(checkpoint) ? 0 : 1;
    }

    public ValueTask<GrainDirectoryTableSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            return ValueTask.FromResult(
                new GrainDirectoryTableSnapshot(
                    _version,
                    GrainDirectoryCheckpointHelper.Clone(_checkpoint)));
        }
    }

    public ValueTask<bool> WriteAsync(
        GrainDirectoryTableWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (request.ExpectedVersion != _version)
            {
                return ValueTask.FromResult(false);
            }

            _checkpoint = GrainDirectoryCheckpointHelper.Clone(request.Checkpoint);
            _version++;
            return ValueTask.FromResult(true);
        }
    }
}
