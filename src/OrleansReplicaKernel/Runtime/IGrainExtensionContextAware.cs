namespace OrleansReplicaKernel.Runtime;

public interface IGrainExtensionContextAware
{
    void SetGrainExtensionContext(IGrainExtensionContext context);
}
