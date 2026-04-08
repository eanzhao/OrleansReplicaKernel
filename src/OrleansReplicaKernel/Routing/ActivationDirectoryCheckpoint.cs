namespace OrleansReplicaKernel.Routing;

public sealed record ActivationDirectoryCheckpoint(
    string NodeName,
    IReadOnlyList<ActivationMetadataRecord> Records);
