namespace OrleansReplicaKernel.Demo;

public interface IPersistentCounterGrain
{
    Task<int> AddAsync(int delta, CancellationToken cancellationToken = default);

    Task<int> GetValueAsync(CancellationToken cancellationToken = default);
}
