namespace OrleansReplicaKernel.Routing;

public sealed record GrainDirectoryCheckpoint(
    IReadOnlyList<GrainOwnerRecord> Records)
{
    public static GrainDirectoryCheckpoint Empty { get; } = new([]);
}
