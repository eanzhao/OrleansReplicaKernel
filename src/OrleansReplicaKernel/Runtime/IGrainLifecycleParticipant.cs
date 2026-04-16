namespace OrleansReplicaKernel.Runtime;

public interface IGrainLifecycleParticipant
{
    ValueTask OnActivateAsync(CancellationToken cancellationToken);

    ValueTask OnDeactivateAsync(ActivationDeactivationReason reason, CancellationToken cancellationToken);
}
