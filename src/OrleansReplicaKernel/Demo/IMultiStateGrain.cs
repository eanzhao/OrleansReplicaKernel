namespace OrleansReplicaKernel.Demo;

public interface IMultiStateGrain
{
    Task SetPrimaryAsync(string value, CancellationToken cancellationToken = default);

    Task SetSecondaryAsync(string value, CancellationToken cancellationToken = default);

    Task<string> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
