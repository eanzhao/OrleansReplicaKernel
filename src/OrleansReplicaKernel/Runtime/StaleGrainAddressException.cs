using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Runtime;

public sealed class StaleGrainAddressException : Exception
{
    public StaleGrainAddressException(
        GrainId grainId,
        string nodeName,
        long messageOwnerVersion,
        long fencedOwnerVersion)
        : base(
            $"Stale grain address for '{grainId}' on node '{nodeName}': request v{messageOwnerVersion} is older than fenced v{fencedOwnerVersion}.")
    {
        GrainId = grainId;
        NodeName = nodeName;
        MessageOwnerVersion = messageOwnerVersion;
        FencedOwnerVersion = fencedOwnerVersion;
    }

    public GrainId GrainId { get; }

    public string NodeName { get; }

    public long MessageOwnerVersion { get; }

    public long FencedOwnerVersion { get; }
}
