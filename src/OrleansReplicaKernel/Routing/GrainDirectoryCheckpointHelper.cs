namespace OrleansReplicaKernel.Routing;

internal static class GrainDirectoryCheckpointHelper
{
    public static GrainDirectoryCheckpoint Clone(GrainDirectoryCheckpoint checkpoint)
        => new(
            checkpoint.Records
                .OrderBy(item => item.GrainId.ToString(), StringComparer.Ordinal)
                .ToArray());

    public static bool IsEmpty(GrainDirectoryCheckpoint checkpoint) => checkpoint.Records.Count == 0;
}
