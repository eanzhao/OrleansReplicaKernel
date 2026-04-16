namespace OrleansReplicaKernel.Transactions;

public sealed class TransactionClient
{
    private readonly TransactionCoordinator _transactionCoordinator;
    private readonly TimeProvider _timeProvider;

    internal TransactionClient(TransactionCoordinator transactionCoordinator, TimeProvider timeProvider)
    {
        _transactionCoordinator = transactionCoordinator ?? throw new ArgumentNullException(nameof(transactionCoordinator));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask RunAsync(
        Func<CancellationToken, ValueTask> callback,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);

        await RunAsync(
            async ct =>
            {
                await callback(ct);
                return true;
            },
            maxRetries,
            cancellationToken);
    }

    public async ValueTask<TResult> RunAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> callback,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (maxRetries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetries), "Transaction retry count must be positive.");
        }

        if (TransactionContext.Current is not null)
        {
            return await callback(cancellationToken);
        }

        TransactionConflictException? lastConflict = null;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var transaction = new TransactionInfo(Guid.NewGuid());
            await _transactionCoordinator.StartAsync(transaction, cancellationToken);

            try
            {
                using (TransactionContext.Enter(transaction))
                {
                    var result = await callback(cancellationToken);
                    await _transactionCoordinator.CommitAsync(transaction.TransactionId, cancellationToken);
                    return result;
                }
            }
            catch (TransactionConflictException exception)
            {
                lastConflict = exception;
                await _transactionCoordinator.AbortAsync(transaction.TransactionId, cancellationToken);
                if (attempt == maxRetries)
                {
                    throw;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), _timeProvider, cancellationToken);
            }
            catch
            {
                await _transactionCoordinator.AbortAsync(transaction.TransactionId, cancellationToken);
                throw;
            }
        }

        if (lastConflict is not null)
        {
            throw lastConflict;
        }

        throw new TransactionAbortedException(Guid.Empty, "transaction retry budget was exhausted");
    }
}
