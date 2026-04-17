using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Versioning;

namespace OrleansReplicaKernel.Routing;

public interface IGrainDirectory
{
    long GetInvalidationVersion();

    long? GetRecordVersion(GrainId grainId);

    GrainOwnerRecord Resolve(GrainId grainId, GrainInterfaceVersionDescriptor? requestedInterface = null);

    GrainOwnerRecord SetOwner(GrainId grainId, string ownerNodeName);

    GrainDirectoryCheckpoint ExportCheckpoint();
}
