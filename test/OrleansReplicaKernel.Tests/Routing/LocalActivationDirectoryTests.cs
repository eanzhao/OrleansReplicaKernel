using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Routing;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Tests.TestSupport;

namespace OrleansReplicaKernel.Tests.Routing;

public sealed class LocalActivationDirectoryTests
{
    private static readonly DateTimeOffset DefaultUtc = new(2026, 04, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StageHandoffState_AppliesToActivationCreatedLater()
    {
        var factory = new TrackingHandoffGrainFactory();
        var timeProvider = new ManualTimeProvider(DefaultUtc);
        await using var directory = CreateDirectory(factory, timeProvider);
        var grainId = new GrainId("TestGrain", "1");
        var address = new GrainAddress("node-a", grainId, OwnerVersion: 3);

        directory.StageHandoffState(
            address,
            CreateHandoffRecord(grainId, 42, timeProvider.GetUtcNow()));

        directory.GetOrCreate(address);

        Assert.Single(factory.CreatedInstances);
        Assert.Equal(42, factory.CreatedInstances[0].State);
    }

    [Fact]
    public async Task StageHandoffState_AppliesImmediatelyToActiveActivationWithSameOwnerVersion()
    {
        var factory = new TrackingHandoffGrainFactory();
        var timeProvider = new ManualTimeProvider(DefaultUtc);
        await using var directory = CreateDirectory(factory, timeProvider);
        var grainId = new GrainId("TestGrain", "1");
        var address = new GrainAddress("node-a", grainId, OwnerVersion: 5);

        directory.GetOrCreate(address);
        directory.StageHandoffState(
            address,
            CreateHandoffRecord(grainId, 99, timeProvider.GetUtcNow()));

        Assert.Single(factory.CreatedInstances);
        Assert.Equal(99, factory.CreatedInstances[0].State);
    }

    [Fact]
    public async Task PrepareHandoffAsync_UsesConfiguredTimeProviderForCapturedUtc()
    {
        var timeProvider = new ManualTimeProvider(DefaultUtc);
        var factory = new TrackingHandoffGrainFactory();
        await using var directory = CreateDirectory(factory, timeProvider);
        var grainId = new GrainId("TestGrain", "1");
        var address = new GrainAddress("node-a", grainId, OwnerVersion: 5);

        directory.GetOrCreate(address);
        factory.CreatedInstances[0].ApplyHandoffState(42);

        timeProvider.Advance(TimeSpan.FromMilliseconds(80));

        var handoff = await directory.PrepareHandoffAsync(address);

        Assert.NotNull(handoff);
        Assert.Equal(42, handoff.Payload);
        Assert.Equal(timeProvider.GetUtcNow(), handoff.CapturedUtc);
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
        var restoreTimeProvider = new ManualTimeProvider(DefaultUtc.AddHours(1));
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
            grainSchedulingPolicies: null,
            timeProvider: restoreTimeProvider,
            checkpoint: checkpoint);

        Assert.Throws<StaleGrainAddressException>(
            () => restored.GetOrCreate(new GrainAddress("node-a", grainId, OwnerVersion: 6)));

        var current = restored.GetOrCreate(currentAddress);

        Assert.Single(restoredFactory.CreatedInstances);
        Assert.Equal(7, current.OwnerVersion);
    }

    [Fact]
    public async Task CollectIdleAsync_UsesPerGrainCollectionAgeLimitWhenPresent()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 15, 0, 0, 0, TimeSpan.Zero));
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
            new LocalCallbackDirectory(),
            timeProvider: timeProvider);

        var initialFast = directory.GetOrCreate(new GrainAddress("node-a", new GrainId("FastGrain", "1"), OwnerVersion: 1));
        var initialSlow = directory.GetOrCreate(new GrainAddress("node-a", new GrainId("SlowGrain", "1"), OwnerVersion: 1));

        timeProvider.Advance(TimeSpan.FromMilliseconds(120));

        var collected = await directory.CollectIdleAsync(TimeSpan.FromMilliseconds(300));

        Assert.Equal(1, collected);
        Assert.Single(fastFactory.CreatedInstances);
        Assert.Single(slowFactory.CreatedInstances);

        var recreatedFast = directory.GetOrCreate(new GrainAddress("node-a", new GrainId("FastGrain", "1"), OwnerVersion: 1));
        var reusedSlow = directory.GetOrCreate(new GrainAddress("node-a", new GrainId("SlowGrain", "1"), OwnerVersion: 1));

        Assert.Equal(2, fastFactory.CreatedInstances.Count);
        Assert.NotSame(initialFast, recreatedFast);
        Assert.Same(initialSlow, reusedSlow);
    }

    private static LocalActivationDirectory CreateDirectory(
        TrackingHandoffGrainFactory factory,
        TimeProvider? timeProvider = null)
        => new(
            new Dictionary<string, Func<object>>
            {
                ["TestGrain"] = factory.Create,
            },
            new Dictionary<string, GrainTypeCollectionPolicy>
            {
                ["TestGrain"] = new(null),
            },
            new LocalCallbackDirectory(timeProvider),
            timeProvider: timeProvider);

    private static ActivationHandoffRecord CreateHandoffRecord(
        GrainId grainId,
        int payload,
        DateTimeOffset capturedUtc)
        => new(grainId, nameof(HandoffTestGrain), payload, capturedUtc);

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
