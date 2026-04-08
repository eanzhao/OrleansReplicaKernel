namespace OrleansReplicaKernel.Routing;

public sealed record GrainDirectoryCheckpoint(
    IReadOnlyList<GrainOwnerRecord> Records);
