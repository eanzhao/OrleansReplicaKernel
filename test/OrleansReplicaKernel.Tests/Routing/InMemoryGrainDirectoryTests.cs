using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Routing;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Tests.TestSupport;

namespace OrleansReplicaKernel.Tests.Routing;

public sealed class InMemoryGrainDirectoryTests
{
    [Fact]
    public void Resolve_CreatesAndCachesOwnerUsingPlacementPolicy()
    {
        var membershipView = new StubClusterMembershipView(
            new Dictionary<string, NodeHealthStatus>
            {
                ["node-a"] = NodeHealthStatus.Healthy,
            });
        var placementPolicy = new StaticPlacementPolicy("node-a");
        var directory = new InMemoryGrainDirectory(
            membershipView,
            placementPolicy,
            new StaticLoadProvider(new PlacementLoadSnapshot([])),
            new StaticRelocationPolicy("node-b"));
        var grainId = new GrainId("Echo", "1");

        var first = directory.Resolve(grainId);
        var second = directory.Resolve(grainId);

        Assert.Equal("node-a", first.OwnerNodeName);
        Assert.Equal(1, first.Version);
        Assert.Equal(first, second);
        Assert.Equal(1, placementPolicy.CallCount);
    }

    [Fact]
    public void Resolve_RelocatesOwnerWhenCurrentNodeBecomesUnhealthy()
    {
        var membershipView = new StubClusterMembershipView(
            new Dictionary<string, NodeHealthStatus>
            {
                ["node-a"] = NodeHealthStatus.Healthy,
                ["node-b"] = NodeHealthStatus.Healthy,
            });
        var directory = new InMemoryGrainDirectory(
            membershipView,
            new StaticPlacementPolicy("node-a"),
            new StaticLoadProvider(new PlacementLoadSnapshot([])),
            new StaticRelocationPolicy("node-b"));
        var grainId = new GrainId("Echo", "1");

        var initial = directory.Resolve(grainId);
        membershipView.SetHealth("node-a", NodeHealthStatus.Unhealthy);

        var relocated = directory.Resolve(grainId);

        Assert.Equal("node-a", initial.OwnerNodeName);
        Assert.Equal("node-b", relocated.OwnerNodeName);
        Assert.Equal(2, relocated.Version);
    }

    [Fact]
    public void ExportCheckpointAndRestore_PreserveOwnerAssignments()
    {
        var membershipView = new StubClusterMembershipView(
            new Dictionary<string, NodeHealthStatus>
            {
                ["node-a"] = NodeHealthStatus.Healthy,
                ["node-b"] = NodeHealthStatus.Healthy,
            });
        var directory = new InMemoryGrainDirectory(
            membershipView,
            new StaticPlacementPolicy("node-a"),
            new StaticLoadProvider(new PlacementLoadSnapshot([])),
            new StaticRelocationPolicy("node-b"));

        var grainA = new GrainId("Echo", "1");
        var grainB = new GrainId("Counter", "2");
        directory.Resolve(grainA);
        var moved = directory.SetOwner(grainB, "node-b");

        var restored = InMemoryGrainDirectory.Restore(
            membershipView,
            new StaticPlacementPolicy("node-a"),
            new StaticLoadProvider(new PlacementLoadSnapshot([])),
            new StaticRelocationPolicy("node-a"),
            directory.ExportCheckpoint());

        Assert.Equal(new GrainOwnerRecord(grainA, "node-a", Version: 1), restored.Resolve(grainA));
        Assert.Equal(moved, restored.Resolve(grainB));
    }

    [Fact]
    public void Resolve_PassesPerGrainPlacementHintToPlacementPolicy()
    {
        var membershipView = new StubClusterMembershipView(
            new Dictionary<string, NodeHealthStatus>
            {
                ["node-a"] = NodeHealthStatus.Healthy,
            });
        var placementPolicy = new StaticPlacementPolicy("node-a");
        var directory = new InMemoryGrainDirectory(
            membershipView,
            placementPolicy,
            new StaticLoadProvider(new PlacementLoadSnapshot([])),
            new StaticRelocationPolicy("node-a"),
            new Dictionary<string, GrainTypePlacementHint>
            {
                ["Counter"] = new(PreferLocalPlacement: true),
            });

        directory.Resolve(new GrainId("Counter", "1"));

        Assert.True(placementPolicy.LastPlacementHint.PreferLocalPlacement);
    }

    private sealed class StaticPlacementPolicy : IPlacementPolicy
    {
        private readonly string _ownerNodeName;

        public StaticPlacementPolicy(string ownerNodeName)
        {
            _ownerNodeName = ownerNodeName;
        }

        public int CallCount { get; private set; }

        public GrainTypePlacementHint LastPlacementHint { get; private set; } = GrainTypePlacementHint.Default;

        public string SelectInitialOwner(
            GrainId grainId,
            IClusterMembershipView membershipView,
            PlacementLoadSnapshot loadSnapshot,
            GrainTypePlacementHint placementHint)
        {
            CallCount++;
            LastPlacementHint = placementHint;
            return _ownerNodeName;
        }
    }

    private sealed class StaticRelocationPolicy : IOwnerRelocationPolicy
    {
        private readonly string _ownerNodeName;

        public StaticRelocationPolicy(string ownerNodeName)
        {
            _ownerNodeName = ownerNodeName;
        }

        public string SelectOwner(
            GrainId grainId,
            string currentOwnerNodeName,
            IClusterMembershipView membershipView)
            => _ownerNodeName;
    }

    private sealed class StaticLoadProvider : IPlacementLoadProvider
    {
        private readonly PlacementLoadSnapshot _snapshot;

        public StaticLoadProvider(PlacementLoadSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public PlacementLoadSnapshot GetSnapshot() => _snapshot;
    }
}
