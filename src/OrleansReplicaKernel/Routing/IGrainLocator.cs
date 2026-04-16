using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Versioning;

namespace OrleansReplicaKernel.Routing;

public interface IGrainLocator
{
    GrainAddress Locate(GrainId grainId, GrainInterfaceVersionDescriptor? requestedInterface = null);

    void Invalidate(GrainId grainId);
}
