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
            new LocalCallbackDirectory(),
            checkpoint);

        Assert.Throws<StaleGrainAddressException>(
            () => restored.GetOrCreate(new GrainAddress("node-a", grainId, OwnerVersion: 6)));

        var current = restored.GetOrCreate(currentAddress);

        Assert.Single(restoredFactory.CreatedInstances);
        Assert.Equal(7, current.OwnerVersion);
    }

    private static LocalActivationDirectory CreateDirectory(TrackingHandoffGrainFactory factory)
        => new(
            new Dictionary<string, Func<object>>
            {
                ["TestGrain"] = factory.Create,
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
}
