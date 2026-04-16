namespace OrleansReplicaKernel.Demo;

public interface ITransactionalAccountGrain
{
    Task<int> AddAsync(int delta, CancellationToken cancellationToken = default);

    Task<int> GetBalanceAsync(CancellationToken cancellationToken = default);
}
