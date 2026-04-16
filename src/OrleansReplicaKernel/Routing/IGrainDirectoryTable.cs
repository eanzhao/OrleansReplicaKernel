namespace OrleansReplicaKernel.Routing;

public interface IGrainDirectoryTable
{
    ValueTask<GrainDirectoryTableSnapshot> ReadAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> WriteAsync(
        GrainDirectoryTableWriteRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record GrainDirectoryTableSnapshot(
    long Version,
    GrainDirectoryCheckpoint Checkpoint);

public sealed record GrainDirectoryTableWriteRequest(
    long ExpectedVersion,
    GrainDirectoryCheckpoint Checkpoint);
