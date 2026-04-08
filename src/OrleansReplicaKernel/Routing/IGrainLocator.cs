using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Routing;

public interface IGrainLocator
{
    GrainAddress Locate(GrainId grainId);

    void Invalidate(GrainId grainId);
}
