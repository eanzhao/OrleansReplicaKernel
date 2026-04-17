using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Demo;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Streaming;
using OrleansReplicaKernel.Tests.TestSupport;

namespace OrleansReplicaKernel.Tests.Streaming;

public sealed class ImplicitStreamSubscriptionTests
{
    [Fact]
    public void Registry_RegisterAndGetImplicitSubscribers_ReturnsGrainIds()
    {
        var registry = new ImplicitStreamSubscriptionRegistry();
        registry.Register("memory", "events", "eventProcessor");

        var subscribers = registry.GetImplicitSubscribers(
            new StreamId("memory", "events", "key-1"));

        Assert.Single(subscribers);
        Assert.Equal(new GrainId("eventProcessor", "key-1"), subscribers[0]);
    }

    [Fact]
    public void Registry_GetImplicitSubscribers_UsesStreamKeyAsGrainKey()
    {
        var registry = new ImplicitStreamSubscriptionRegistry();
        registry.Register("memory", "events", "processor");

        var subscribers1 = registry.GetImplicitSubscribers(
            new StreamId("memory", "events", "alpha"));
        var subscribers2 = registry.GetImplicitSubscribers(
            new StreamId("memory", "events", "beta"));

        Assert.Equal("alpha", subscribers1[0].Key);
        Assert.Equal("beta", subscribers2[0].Key);
    }

    [Fact]
    public void Registry_MultipleGrainTypes_ReturnsBoth()
    {
        var registry = new ImplicitStreamSubscriptionRegistry();
        registry.Register("memory", "events", "processorA");
        registry.Register("memory", "events", "processorB");

        var subscribers = registry.GetImplicitSubscribers(
            new StreamId("memory", "events", "key-1"));

        Assert.Equal(2, subscribers.Count);
        Assert.Contains(subscribers, s => s.GrainType == "processorA");
        Assert.Contains(subscribers, s => s.GrainType == "processorB");
    }

    [Fact]
    public void Registry_DuplicateRegistration_DoesNotDuplicate()
    {
        var registry = new ImplicitStreamSubscriptionRegistry();
        registry.Register("memory", "events", "processor");
        registry.Register("memory", "events", "processor");

        var subscribers = registry.GetImplicitSubscribers(
            new StreamId("memory", "events", "key-1"));

        Assert.Single(subscribers);
    }

    [Fact]
    public void Registry_NonMatchingProvider_ReturnsEmpty()
    {
        var registry = new ImplicitStreamSubscriptionRegistry();
        registry.Register("memory", "events", "processor");

        var subscribers = registry.GetImplicitSubscribers(
            new StreamId("other-provider", "events", "key-1"));

        Assert.Empty(subscribers);
    }

    [Fact]
    public void Registry_NonMatchingNamespace_ReturnsEmpty()
    {
        var registry = new ImplicitStreamSubscriptionRegistry();
        registry.Register("memory", "events", "processor");

        var subscribers = registry.GetImplicitSubscribers(
            new StreamId("memory", "other-namespace", "key-1"));

        Assert.Empty(subscribers);
    }

    [Fact]
    public void Registry_HasImplicitSubscribers_ReturnsTrueForRegistered()
    {
        var registry = new ImplicitStreamSubscriptionRegistry();
        registry.Register("memory", "events", "processor");

        Assert.True(registry.HasImplicitSubscribers("memory", "events"));
        Assert.False(registry.HasImplicitSubscribers("memory", "other"));
        Assert.False(registry.HasImplicitSubscribers("other", "events"));
    }

    [Fact]
    public async Task ImplicitSubscription_AutoSubscribesAndReceivesEvents()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 04, 17, 0, 0, 0, TimeSpan.Zero));
        var storagePath = CreateStoragePath("implicit");

        try
        {
            await using var host = CreateHost(timeProvider, storagePath);

            var publisher = host.GetGrain<IImplicitStreamPublisherGrain>("implicit-key");

            await publisher.PublishAsync("hello");
            await publisher.PublishAsync("world");

            var snapshot = await WaitForImplicitSnapshotAsync(
                host, timeProvider, "implicit-key", "hello|world");

            Assert.Equal("hello|world", snapshot);
        }
        finally
        {
            DeleteStorageArtifacts(storagePath);
        }
    }

    private static OrleansReplicaKernelHost CreateHost(
        TimeProvider timeProvider,
        string storagePath)
    {
        return new OrleansReplicaKernelBuilder()
            .AddGeneratedGrainImplementationsFromAssembly(typeof(EchoGrain).Assembly)
            .AddGeneratedGrainReferencesFromAssembly(typeof(EchoGrainReference).Assembly)
            .WithTimeProvider(timeProvider)
            .UseFileGrainStorage(storagePath)
            .UseMemoryStreamProvider("memory", maxBatchSize: 10, TimeSpan.FromMilliseconds(10))
            .Build("dev-node-1");
    }

    private static async Task<string> WaitForImplicitSnapshotAsync(
        OrleansReplicaKernelHost host,
        ManualTimeProvider timeProvider,
        string key,
        string expectedReceived)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(20));
            await AsyncTestSync.YieldUntilDispatchAsync(iterations: 4);

            var grain = host.GetGrain<IImplicitSubscriberGrain>(key);
            var received = await grain.GetReceivedSnapshotAsync();
            if (received == expectedReceived)
            {
                return received;
            }
        }

        var final = host.GetGrain<IImplicitSubscriberGrain>(key);
        return await final.GetReceivedSnapshotAsync();
    }

    private static string CreateStoragePath(string prefix)
        => Path.Combine(
            Path.GetTempPath(),
            "OrleansReplicaKernel.Tests",
            "implicit-stream",
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
