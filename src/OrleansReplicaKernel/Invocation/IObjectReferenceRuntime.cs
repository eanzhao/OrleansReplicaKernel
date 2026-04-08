namespace OrleansReplicaKernel.Invocation;

public interface IObjectReferenceRuntime : IInvocationRuntime
{
    ObjectReferenceFactoryRegistry ObjectReferences { get; }
}
