namespace OrleansReplicaKernel.Versioning;

public interface IGrainInterfaceVersionTable
{
    ValueTask<GrainInterfaceVersionTableSnapshot> ReadAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> WriteAsync(
        GrainInterfaceVersionTableWriteRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record GrainInterfaceVersionTableSnapshot(
    long Version,
    GrainInterfaceVersionManifestCheckpoint Checkpoint);

public sealed record GrainInterfaceVersionTableWriteRequest(
    long ExpectedVersion,
    GrainInterfaceVersionManifestCheckpoint Checkpoint);
