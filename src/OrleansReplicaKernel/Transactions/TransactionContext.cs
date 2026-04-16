namespace OrleansReplicaKernel.Transactions;

public static class TransactionContext
{
    private static readonly AsyncLocal<TransactionInfo?> CurrentSlot = new();

    public static TransactionInfo? Current => CurrentSlot.Value;

    internal static IDisposable Enter(TransactionInfo? transactionInfo)
    {
        var previous = CurrentSlot.Value;
        CurrentSlot.Value = transactionInfo;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly TransactionInfo? _previous;
        private int _disposed;

        public Scope(TransactionInfo? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            CurrentSlot.Value = _previous;
        }
    }
}
