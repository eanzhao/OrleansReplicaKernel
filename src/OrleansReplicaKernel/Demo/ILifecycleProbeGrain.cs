namespace OrleansReplicaKernel.Demo;

public interface ILifecycleProbeGrain
{
    Task<int> GetActivationCountAsync(CancellationToken cancellationToken = default);

    Task<int> GetDeactivationCountAsync(CancellationToken cancellationToken = default);

    Task<string> GetLastDeactivationReasonAsync(CancellationToken cancellationToken = default);
}
