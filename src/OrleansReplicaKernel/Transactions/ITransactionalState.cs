namespace OrleansReplicaKernel.Transactions;

public interface ITransactionalState<TState>
{
    ValueTask<TResult> PerformReadAsync<TResult>(
        Func<TState, TResult> read,
        CancellationToken cancellationToken = default);

    ValueTask<TResult> PerformUpdateAsync<TResult>(
        Func<TState, TResult> update,
        CancellationToken cancellationToken = default);

    ValueTask PerformUpdateAsync(
        Action<TState> update,
        CancellationToken cancellationToken = default);
}
