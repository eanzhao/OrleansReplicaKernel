using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Routing;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Tests.Routing;

public sealed class LocalActivationDirectoryTests
{
    [Fact]
    public async Task StageHandoffState_AppliesToActivationCreatedLater()
    {
        var factory = new TrackingHandoffGrainFactory();
        await using var directory = CreateDirectory(factory);
        var grainId = new GrainId("TestGrain", "1");
        var address = new GrainAddress("node-a", grainId, OwnerVersion: 3);

        directory.StageHandoffState(
            address,
            new ActivationHandoffRecord(grainId, nameof(HandoffTestGrain), 42, DateTimeOffset.UtcNow));

        directory.GetOrCreate(address);

        Assert.Single(factory.CreatedInstances);
        Assert.Equal(42, factory.CreatedInstances[0].State);
    }

    [Fact]
    public async Task StageHandoffState_AppliesImmediatelyToActiveActivationWithSameOwnerVersion()
    {
        var factory = new TrackingHandoffGrainFactory();
        await using var directory = CreateDirectory(factory);
        var grainId = new GrainId("TestGrain", "1");
        var address = new GrainAddress("node-a", grainId, OwnerVersion: 5);

        directory.GetOrCreate(address);
        directory.StageHandoffState(
            address,
            new ActivationHandoffRecord(grainId, nameof(HandoffTestGrain), 99, DateTimeOffset.UtcNow));

        Assert.Single(factory.CreatedInstances);
        Assert.Equal(99, factory.CreatedInstances[0].State);
    }

    [Fact]
    public async Task Fence_RejectsOlderOwnerVersionRequests()
    {
        await using var directory = CreateDirectory(new TrackingHandoffGrainFactory());
        var grainId = new GrainId("TestGrain", "1");

        directory.Fence(new GrainAddress("node-a", grainId, OwnerVersion: 5));

        var exception = Assert.Throws<StaleGrainAddressException>(
            () => directory.GetOrCreate(new GrainAddress("node-a", grainId, OwnerVersion: 4)));

        Assert.Equal(4, exception.MessageOwnerVersion);
        Assert.Equal(5, exception.FencedOwnerVersion);
    }

    [Fact]
    public async Task Restore_RejectsRecoveredStaleRequestsAndAllowsCurrentOwnerVersion()
    {
        var seedFactory = new TrackingHandoffGrainFactory();
        var grainId = new GrainId("TestGrain", "1");
        var currentAddress = new GrainAddress("node-a", grainId, OwnerVersion: 7);
        ActivationDirectoryCheckpoint checkpoint;

        await using (var source = CreateDirectory(seedFactory))
        {
            source.GetOrCreate(currentAddress);
            checkpoint = source.ExportCheckpoint("node-a");
        }

        var restoredFactory = new TrackingHandoffGrainFactory();
        await using var restored = LocalActivationDirectory.Restore(
            new Dictionary<string, Func<object>>
            {
                ["TestGrain"] = restoredFactory.Create,
            },
            new Dictionary<string, GrainTypeCollectionPolicy>
            {
                ["TestGrain"] = new(null),
            },
            new LocalCallbackDirectory(),
            checkpoint);

        Assert.Throws<StaleGrainAddressException>(
            () => restored.GetOrCreate(new GrainAddress("node-a", grainId, OwnerVersion: 6)));

        var current = restored.GetOrCreate(currentAddress);

        Assert.Single(restoredFactory.CreatedInstances);
        Assert.Equal(7, current.OwnerVersion);
    }

    [Fact]
    public async Task CollectIdleAsync_UsesPerGrainCollectionAgeLimitWhenPresent()
    {
        var fastFactory = new TrackingInstanceFactory<PassiveTestGrain>();
        var slowFactory = new TrackingInstanceFactory<PassiveTestGrain>();
        await using var directory = new LocalActivationDirectory(
            new Dictionary<string, Func<object>>
            {
                ["FastGrain"] = fastFactory.Create,
                ["SlowGrain"] = slowFactory.Create,
            },
            new Dictionary<string, GrainTypeCollectionPolicy>
            {
                ["FastGrain"] = new(TimeSpan.FromMilliseconds(50)),
                ["SlowGrain"] = new(TimeSpan.FromSeconds(5)),
            },
            new LocalCallbackDirectory());

        var initialFast = directory.GetOrCreate(new GrainAddress("node-a", new GrainId("FastGrain", "1"), OwnerVersion: 1));
        var initialSlow = directory.GetOrCreate(new GrainAddress("node-a", new GrainId("SlowGrain", "1"), OwnerVersion: 1));

        await Task.Delay(TimeSpan.FromMilliseconds(120));

        var collected = await directory.CollectIdleAsync(TimeSpan.FromMilliseconds(300));

        Assert.Equal(1, collected);
        Assert.Equal(1, fastFactory.CreatedInstances.Count);
        Assert.Equal(1, slowFactory.CreatedInstances.Count);

        var recreatedFast = directory.GetOrCreate(new GrainAddress("node-a", new GrainId("FastGrain", "1"), OwnerVersion: 1));
        var reusedSlow = directory.GetOrCreate(new GrainAddress("node-a", new GrainId("SlowGrain", "1"), OwnerVersion: 1));

        Assert.Equal(2, fastFactory.CreatedInstances.Count);
        Assert.NotSame(initialFast, recreatedFast);
        Assert.Same(initialSlow, reusedSlow);
    }

    private static LocalActivationDirectory CreateDirectory(TrackingHandoffGrainFactory factory)
        => new(
            new Dictionary<string, Func<object>>
            {
                ["TestGrain"] = factory.Create,
            },
            new Dictionary<string, GrainTypeCollectionPolicy>
            {
                ["TestGrain"] = new(null),
            },
            new LocalCallbackDirectory());

    private sealed class TrackingHandoffGrainFactory
    {
        public List<HandoffTestGrain> CreatedInstances { get; } = [];

        public object Create()
        {
            var grain = new HandoffTestGrain();
            CreatedInstances.Add(grain);
            return grain;
        }
    }

    private sealed class HandoffTestGrain : IActivationHandoffParticipant
    {
        public int State { get; private set; }

        public object? CaptureHandoffState() => State;

        public void ApplyHandoffState(object? state)
        {
            State = state is int value ? value : 0;
        }
    }

    private sealed class TrackingInstanceFactory<TInstance>
        where TInstance : class, new()
    {
        public List<TInstance> CreatedInstances { get; } = [];

        public object Create()
        {
            var instance = new TInstance();
            CreatedInstances.Add(instance);
            return instance;
        }
    }

    private sealed class PassiveTestGrain
    {
    }
}
