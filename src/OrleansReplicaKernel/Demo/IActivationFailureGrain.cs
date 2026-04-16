namespace OrleansReplicaKernel.Demo;

public interface IActivationFailureGrain
{
    Task<int> GetActivationCountAsync(CancellationToken cancellationToken = default);
}
