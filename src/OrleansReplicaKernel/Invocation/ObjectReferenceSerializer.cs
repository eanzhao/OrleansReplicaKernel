using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Invocation;

public static class ObjectReferenceSerializer
{
    public static ObjectReferenceData Export<TInterface>(TInterface reference)
        where TInterface : class
    {
        if (reference is not IObjectReference objectReference)
        {
            throw new InvalidOperationException(
                $"Object of type '{reference.GetType().Name}' is not a runtime object reference for '{typeof(TInterface).Name}'.");
        }

        return new ObjectReferenceData(
            ObjectReferenceFactoryRegistry.GetInterfaceName<TInterface>(),
            objectReference.GrainId);
    }

    public static TInterface Rehydrate<TInterface>(
        ObjectReferenceData data,
        IInvocationRuntime runtime,
        ObjectReferenceFactoryRegistry registry)
        where TInterface : class
        => registry.Rehydrate<TInterface>(data, runtime);
}
