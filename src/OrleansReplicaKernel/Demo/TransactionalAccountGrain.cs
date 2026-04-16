using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Transactions;

namespace OrleansReplicaKernel.Demo;

public sealed partial class TransactionalAccountGrain : ITransactionalAccountGrain
{
    private readonly ITransactionalState<TransactionalAccountState> _balanceState;

    public TransactionalAccountGrain(
        [TransactionalState("balance")] ITransactionalState<TransactionalAccountState> balanceState)
    {
        _balanceState = balanceState;
    }

    public async Task<int> AddAsync(int delta, CancellationToken cancellationToken = default)
    {
        var total = await _balanceState.PerformUpdateAsync(
            state =>
            {
                state.Balance += delta;
                return state.Balance;
            },
            cancellationToken);
        TraceLog.Write("grain", $"TransactionalAccountGrain handle AddAsync({delta}) total={total}");
        return total;
    }

    public async Task<int> GetBalanceAsync(CancellationToken cancellationToken = default)
        => await _balanceState.PerformReadAsync(state => state.Balance, cancellationToken);
}

public sealed class TransactionalAccountState
{
    public int Balance { get; set; }
}
