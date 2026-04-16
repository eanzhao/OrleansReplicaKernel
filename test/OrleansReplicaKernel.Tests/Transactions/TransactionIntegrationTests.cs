using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Demo;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Storage;
using OrleansReplicaKernel.Transactions;

namespace OrleansReplicaKernel.Tests.Transactions;

public sealed class TransactionIntegrationTests
{
    [Fact]
    public async Task Transactions_CommitAllParticipants_OrRollbackAllParticipants()
    {
        var storagePath = CreateStoragePath("atomicity");

        try
        {
            await using var host = CreateHost(storagePath);
            var left = host.GetGrain<ITransactionalAccountGrain>("left");
            var right = host.GetGrain<ITransactionalAccountGrain>("right");

            await host.RunTransactionAsync(async cancellationToken =>
            {
                await left.AddAsync(10, cancellationToken);
                await right.AddAsync(5, cancellationToken);
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                host.RunTransactionAsync(async cancellationToken =>
                {
                    await left.AddAsync(-3, cancellationToken);
                    await right.AddAsync(3, cancellationToken);
                    throw new InvalidOperationException("boom");
                }).AsTask());

            Assert.Equal(10, await left.GetBalanceAsync());
            Assert.Equal(5, await right.GetBalanceAsync());

            await host.RunTransactionAsync(async cancellationToken =>
            {
                await left.AddAsync(-3, cancellationToken);
                await right.AddAsync(3, cancellationToken);
            });

            Assert.Equal(7, await left.GetBalanceAsync());
            Assert.Equal(8, await right.GetBalanceAsync());
        }
        finally
        {
            DeleteStorageArtifacts(storagePath);
        }
    }

    [Fact]
    public async Task Transactions_RetryAfterWriteConflict_AndEventuallyCommit()
    {
        var storagePath = CreateStoragePath("retry");

        try
        {
            await using var host = CreateHost(storagePath);
            var account = host.GetGrain<ITransactionalAccountGrain>("shared");

            var firstTransaction = host.RunTransactionAsync(
                async cancellationToken =>
                {
                    await account.AddAsync(10, cancellationToken);
                    await host.DelayAsync(TimeSpan.FromMilliseconds(80), cancellationToken);
                },
                maxRetries: 1).AsTask();

            await host.DelayAsync(TimeSpan.FromMilliseconds(10));

            var secondTransaction = host.RunTransactionAsync(
                async cancellationToken =>
                {
                    await account.AddAsync(5, cancellationToken);
                },
                maxRetries: 20).AsTask();

            await Task.WhenAll(firstTransaction, secondTransaction);

            Assert.Equal(15, await account.GetBalanceAsync());
        }
        finally
        {
            DeleteStorageArtifacts(storagePath);
        }
    }

    [Fact]
    public async Task Transactions_ResolvePreparedParticipant_OnNextAccess()
    {
        var storagePath = CreateStoragePath("recovery");
        var transactionId = Guid.NewGuid();
        var participant = new TransactionParticipantReference(
            new GrainId("transactionalAccount", "recovery"),
            "balance",
            StorageName: null);
        var utcNow = DateTimeOffset.UtcNow;

        try
        {
            var storage = new FileGrainStorage(storagePath);
            var resolver = new GrainStorageResolver(
                storage,
                new Dictionary<string, IGrainStorage>(StringComparer.Ordinal));
            var coordinator = new TransactionCoordinator(resolver, TimeProvider.System);

            var transactionRecord = new GrainState<TransactionRecord>
            {
                State = new TransactionRecord
                {
                    TransactionId = transactionId,
                    Status = TransactionStatus.Committed,
                    Participants = [participant],
                    CreatedUtc = utcNow,
                    UpdatedUtc = utcNow
                }
            };
            await storage.WriteStateAsync(
                TransactionStorageOperations.TransactionStateName,
                TransactionStorageOperations.CreateTransactionRecordGrainId(transactionId),
                transactionRecord);

            var participantRecord = new GrainState<TransactionalStateParticipantRecord>
            {
                State = new TransactionalStateParticipantRecord
                {
                    CommittedVersion = 0,
                    CommittedState = TransactionStateSerializer.Serialize(new TransactionalAccountState
                    {
                        Balance = 2
                    }),
                    LockedTransactionId = transactionId,
                    PendingWrite = new TransactionParticipantWrite
                    {
                        TransactionId = transactionId,
                        State = TransactionStateSerializer.Serialize(new TransactionalAccountState
                        {
                            Balance = 9
                        }),
                        BaseVersion = 0,
                        Status = TransactionParticipantWriteStatus.Prepared,
                        UpdatedUtc = utcNow
                    }
                }
            };
            await storage.WriteStateAsync(
                TransactionStorageOperations.ParticipantStateName,
                TransactionStorageOperations.CreateParticipantRecordGrainId(participant),
                participantRecord);

            var transactionalState = new TransactionalState<TransactionalAccountState>(
                participant.GrainId,
                participant.StateName,
                participant.StorageName,
                storage,
                coordinator,
                TimeProvider.System);

            var committedBalance = await transactionalState.PerformReadAsync(state => state.Balance);
            Assert.Equal(9, committedBalance);

            var resolved = await TransactionStorageOperations.ReadRecordAsync<TransactionalStateParticipantRecord>(
                storage,
                TransactionStorageOperations.ParticipantStateName,
                TransactionStorageOperations.CreateParticipantRecordGrainId(participant));
            Assert.True(resolved.RecordExists);
            Assert.Equal(1, resolved.State.CommittedVersion);
            Assert.Null(resolved.State.LockedTransactionId);
            Assert.Null(resolved.State.PendingWrite);
        }
        finally
        {
            DeleteStorageArtifacts(storagePath);
        }
    }

    private static OrleansReplicaKernelHost CreateHost(string storagePath)
        => new OrleansReplicaKernelBuilder()
            .AddGeneratedGrainImplementationsFromAssembly(typeof(EchoGrain).Assembly)
            .AddGeneratedGrainReferencesFromAssembly(typeof(EchoGrainReference).Assembly)
            .UseFileGrainStorage(storagePath)
            .Build("dev-node-1");

    private static string CreateStoragePath(string prefix)
        => Path.Combine(
            Path.GetTempPath(),
            "OrleansReplicaKernel.Tests",
            "transactions",
            prefix + "-" + Guid.NewGuid().ToString("N") + ".json");

    private static void DeleteStorageArtifacts(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var lockPath = path + ".lock";
        if (File.Exists(lockPath))
        {
            File.Delete(lockPath);
        }
    }
}
