using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Routing;

public interface IGrainRouter
{
    GrainAddress Route(InvocationMessage message);
}
