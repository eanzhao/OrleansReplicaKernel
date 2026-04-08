using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Runtime;

public sealed class ActivationQuiescingException : InvalidOperationException
{
    public ActivationQuiescingException(GrainId grainId)
        : base($"Activation '{grainId}' is quiescing for handoff.")
    {
        GrainId = grainId;
    }

    public GrainId GrainId { get; }
}
