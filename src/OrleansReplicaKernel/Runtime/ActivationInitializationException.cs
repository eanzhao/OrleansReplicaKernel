using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Runtime;

public sealed class ActivationInitializationException : InvalidOperationException
{
    public ActivationInitializationException(
        GrainId grainId,
        string? message = null,
        Exception? innerException = null)
        : base(
            message ?? $"Activation '{grainId}' failed during OnActivateAsync.",
            innerException)
    {
        GrainId = grainId;
    }

    public GrainId GrainId { get; }
}
