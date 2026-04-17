using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Demo;
using OrleansReplicaKernel.Streaming;
using OrleansReplicaKernel.Tests.TestSupport;

namespace OrleansReplicaKernel.Tests.Streaming;

public sealed class StreamBackpressureTests
{
    [Fact]
    public async Task DeliveryFailure_RetriesToSubscriber()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 04, 17, 0, 0, 0, TimeSpan.Zero));
        var storagePath = CreateStoragePath("backpressure-retry");

        try
        {
            await using var host = CreateHost(timeProvider, storagePath, maxDeliveryAttempts: 5);

            var subscriber = host.GetGrain<IFailingStreamSubscriberGrain>("bp-key");
            var publisher = host.GetGrain<IFailingStreamPublisherGrain>("bp-key");

            Assert.Equal(0, await subscriber.GetDeliveryAttemptCountAsync());

            await publisher.PublishAsync("trigger-failure");

            for (var i = 0; i < 80; i++)
            {
                timeProvider.Advance(TimeSpan.FromMilliseconds(100));
                await AsyncTestSync.YieldUntilDispatchAsync(iterations: 4);
            }

            var attempts = await subscriber.GetDeliveryAttemptCountAsync();
            Assert.True(attempts >= 2,
                $"Expected at least 2 delivery attempts with backoff, got {attempts}");
        }
        finally
        {
            DeleteStorageArtifacts(storagePath);
        }
    }

    [Fact]
    public async Task DeliveryFailure_StopsAfterMaxAttempts()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 04, 17, 0, 0, 0, TimeSpan.Zero));
        var storagePath = CreateStoragePath("backpressure-max");

        try
        {
            await using var host = CreateHost(timeProvider, storagePath, maxDeliveryAttempts: 3);

            var subscriber = host.GetGrain<IFailingStreamSubscriberGrain>("bp-max");
            var publisher = host.GetGrain<IFailingStreamPublisherGrain>("bp-max");

            Assert.Equal(0, await subscriber.GetDeliveryAttemptCountAsync());

            await publisher.PublishAsync("will-dead-letter");

            for (var i = 0; i < 100; i++)
            {
                timeProvider.Advance(TimeSpan.FromMilliseconds(200));
                await AsyncTestSync.YieldUntilDispatchAsync(iterations: 6);
            }

            var attempts = await subscriber.GetDeliveryAttemptCountAsync();
            Assert.True(attempts >= 2,
                $"Expected at least 2 delivery attempts, got {attempts}");
            Assert.True(attempts <= 4,
                $"Expected at most ~3 delivery attempts (MaxDeliveryAttempts=3), got {attempts}");
        }
        finally
        {
            DeleteStorageArtifacts(storagePath);
        }
    }

    [Fact]
    public void ImplicitStreamSubscriptionAttribute_StoresProviderAndNamespace()
    {
        var attr = new ImplicitStreamSubscriptionAttribute("provider-x", "namespace-y");

        Assert.Equal("provider-x", attr.ProviderName);
        Assert.Equal("namespace-y", attr.StreamNamespace);
    }

    [Fact]
    public void ImplicitStreamSubscriptionAttribute_ThrowsOnNullOrWhitespace()
    {
        Assert.Throws<ArgumentException>(
            () => new ImplicitStreamSubscriptionAttribute("", "ns"));
        Assert.Throws<ArgumentException>(
            () => new ImplicitStreamSubscriptionAttribute("provider", ""));
        Assert.Throws<ArgumentException>(
            () => new ImplicitStreamSubscriptionAttribute(" ", "ns"));
        Assert.Throws<ArgumentException>(
            () => new ImplicitStreamSubscriptionAttribute("provider", " "));
    }

    private static OrleansReplicaKernelHost CreateHost(
        TimeProvider timeProvider,
        string storagePath,
        int maxDeliveryAttempts)
        => new OrleansReplicaKernelBuilder()
            .AddGeneratedGrainImplementationsFromAssembly(typeof(EchoGrain).Assembly)
            .AddGeneratedGrainReferencesFromAssembly(typeof(EchoGrainReference).Assembly)
            .WithTimeProvider(timeProvider)
            .UseFileGrainStorage(storagePath)
            .UseMemoryStreamProvider("memory", maxBatchSize: 10,
                TimeSpan.FromMilliseconds(10), maxDeliveryAttempts)
            .Build("dev-node-1");

    private static string CreateStoragePath(string prefix)
        => Path.Combine(
            Path.GetTempPath(),
            "OrleansReplicaKernel.Tests",
            "backpressure",
            prefix + "-" + Guid.NewGuid().ToString("N") + ".json");

    private static void DeleteStorageArtifacts(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
        var lockPath = path + ".lock";
        if (File.Exists(lockPath))
            File.Delete(lockPath);
    }
}
