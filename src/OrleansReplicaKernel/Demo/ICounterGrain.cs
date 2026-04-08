namespace OrleansReplicaKernel.Demo;

public interface ICounterGrain
{
    Task<int> AddAsync(int delta, CancellationToken cancellationToken = default);
}
