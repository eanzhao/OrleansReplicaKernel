using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Invocation;

public interface IObjectReference
{
    GrainId GrainId { get; }
}
