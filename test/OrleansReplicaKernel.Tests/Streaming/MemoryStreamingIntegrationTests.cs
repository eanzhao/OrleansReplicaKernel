using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Demo;
using OrleansReplicaKernel.Tests.TestSupport;

namespace OrleansReplicaKernel.Tests.Streaming;

public sealed class MemoryStreamingIntegrationTests
{
    [Fact]
    public async Task MemoryStreams_ReactivateSubscriber_AndContinueFromLastSequence()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        var storagePath = CreateStoragePath("reactivate");

        try
        {
            await using var host = CreateHost(timeProvider, storagePath, maxBatchSize: 2);
            var subscriber = host.GetGrain<IStreamSubscriberGrain>("alpha");
            var publisher = host.GetGrain<IStreamPublisherGrain>("alpha");

            Assert.Equal("<none>", await subscriber.GetReceivedSnapshotAsync());

            await publisher.PublishAsync("one");
            await publisher.PublishAsync("two");

            var initialSnapshot = await WaitForSnapshotAsync(
                host,
                timeProvider,
                "alpha",
                expectedReceived: "one|two");
            Assert.Equal("one|two", initialSnapshot.Received);
            Assert.Equal(2, initialSnapshot.LastSequenceToken);

            await host.DeactivateGrainAsync<IStreamSubscriberGrain>("alpha");

            await publisher.PublishAsync("three");

            var afterReactivate = await WaitForSnapshotAsync(
                host,
                timeProvider,
                "alpha",
                expectedReceived: "one|two|three");
            Assert.Equal("one|two|three", afterReactivate.Received);
            Assert.Equal(3, afterReactivate.LastSequenceToken);
        }
        finally
        {
            DeleteStorageArtifacts(storagePath);
        }
    }

    [Fact]
    public async Task MemoryStreams_SplitDeliveriesByConfiguredBatchSize()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        var storagePath = CreateStoragePath("batch");

        try
        {
            await using var host = CreateHost(timeProvider, storagePath, maxBatchSize: 2);
            var subscriber = host.GetGrain<IStreamSubscriberGrain>("batch");
            var publisher = host.GetGrain<IStreamPublisherGrain>("batch");

            Assert.Equal("<none>", await subscriber.GetReceivedSnapshotAsync());

            await publisher.PublishAsync("one");
            await publisher.PublishAsync("two");
            await publisher.PublishAsync("three");

            var snapshot = await WaitForSnapshotAsync(
                host,
                timeProvider,
                "batch",
                expectedReceived: "one|two|three");
            Assert.Equal("2|1", snapshot.BatchSizes);
            Assert.Equal(3, snapshot.LastSequenceToken);
        }
        finally
        {
            DeleteStorageArtifacts(storagePath);
        }
    }

    private static OrleansReplicaKernelHost CreateHost(
        TimeProvider timeProvider,
        string storagePath,
        int maxBatchSize)
        => new OrleansReplicaKernelBuilder()
            .AddGeneratedGrainImplementationsFromAssembly(typeof(EchoGrain).Assembly)
            .AddGeneratedGrainReferencesFromAssembly(typeof(EchoGrainReference).Assembly)
            .WithTimeProvider(timeProvider)
            .UseFileGrainStorage(storagePath)
            .UseMemoryStreamProvider("memory", maxBatchSize, TimeSpan.FromMilliseconds(10))
            .Build("dev-node-1");

    private static async Task<(string Received, string BatchSizes, long LastSequenceToken)> WaitForSnapshotAsync(
        OrleansReplicaKernelHost host,
        ManualTimeProvider timeProvider,
        string key,
        string expectedReceived)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(20));
            await AsyncTestSync.YieldUntilDispatchAsync(iterations: 4);

            var grain = host.GetGrain<IStreamSubscriberGrain>(key);
            var received = await grain.GetReceivedSnapshotAsync();
            if (received == expectedReceived)
            {
                return (
                    received,
                    await grain.GetBatchSizesSnapshotAsync(),
                    await grain.GetLastSequenceTokenAsync());
            }
        }

        var final = host.GetGrain<IStreamSubscriberGrain>(key);
        return (
            await final.GetReceivedSnapshotAsync(),
            await final.GetBatchSizesSnapshotAsync(),
            await final.GetLastSequenceTokenAsync());
    }

    private static string CreateStoragePath(string prefix)
        => Path.Combine(
            Path.GetTempPath(),
            "OrleansReplicaKernel.Tests",
            "memory-streaming",
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
