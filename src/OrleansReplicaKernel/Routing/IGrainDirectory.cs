using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Routing;

public interface IGrainDirectory
{
    long GetInvalidationVersion();

    GrainOwnerRecord Resolve(GrainId grainId);

    GrainOwnerRecord SetOwner(GrainId grainId, string ownerNodeName);

    GrainDirectoryCheckpoint ExportCheckpoint();
}
