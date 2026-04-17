using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Routing;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Tests.TestSupport;
using OrleansReplicaKernel.Versioning;

namespace OrleansReplicaKernel.Tests.Routing;

public sealed class PersistentGrainDirectoryTests
{
    [Fact]
    public void ConsistentHashDirectoryPartitionResolver_SelectsStableOwnerAndMigratesWhenOwnerFails()
    {
        var resolver = new ConsistentHashDirectoryPartitionResolver();
        var grainId = new GrainId("Echo", "partitioned");
        var initialView = new StubClusterMembershipView(
            new Dictionary<string, NodeHealthStatus>
            {
                ["node-a"] = NodeHealthStatus.Healthy,
                ["node-b"] = NodeHealthStatus.Healthy,
                ["node-c"] = NodeHealthStatus.Healthy,
            });

        var initialOwner = resolver.SelectOwner(grainId, initialView);

        var withoutNonOwner = new StubClusterMembershipView(
            new Dictionary<string, NodeHealthStatus>
            {
                ["node-a"] = NodeHealthStatus.Healthy,
                ["node-b"] = NodeHealthStatus.Healthy,
                ["node-c"] = NodeHealthStatus.Healthy,
            });
        var nonOwnerNodeName = withoutNonOwner.GetHealthyMembers()
            .First(nodeName => !string.Equals(nodeName, initialOwner, StringComparison.Ordinal));
        withoutNonOwner.SetHealth(nonOwnerNodeName, NodeHealthStatus.Unhealthy);

        initialView.SetHealth(initialOwner, NodeHealthStatus.Unhealthy);
        var migratedOwner = resolver.SelectOwner(grainId, initialView);

        Assert.Equal(initialOwner, resolver.SelectOwner(grainId, new StubClusterMembershipView(
            new Dictionary<string, NodeHealthStatus>
            {
                ["node-a"] = NodeHealthStatus.Healthy,
                ["node-b"] = NodeHealthStatus.Healthy,
                ["node-c"] = NodeHealthStatus.Healthy,
            })));
        Assert.Equal(initialOwner, resolver.SelectOwner(grainId, withoutNonOwner));
        Assert.NotEqual(initialOwner, migratedOwner);
    }

    [Fact]
    public async Task FileGrainDirectoryTable_WriteAsync_RejectsStaleVersionWrites()
    {
        var directoryFile = CreateDirectoryFilePath();
        try
        {
            var table = new FileGrainDirectoryTable(directoryFile);
            var grainId = new GrainId("Echo", "stale");
            var initial = await table.ReadAsync();
            var registeredCheckpoint = new GrainDirectoryCheckpoint(
                [new GrainOwnerRecord(grainId, "node-a", Version: 1)]);

            var registered = await table.WriteAsync(
                new GrainDirectoryTableWriteRequest(initial.Version, registeredCheckpoint));
            var staleUpdate = await table.WriteAsync(
                new GrainDirectoryTableWriteRequest(
                    initial.Version,
                    new GrainDirectoryCheckpoint(
                        [new GrainOwnerRecord(grainId, "node-b", Version: 2)])));
            var current = await table.ReadAsync();

            Assert.True(registered);
            Assert.False(staleUpdate);
            Assert.Equal(1, current.Version);
            Assert.Equal(registeredCheckpoint.Records, current.Checkpoint.Records);
        }
        finally
        {
            DeleteDirectoryArtifacts(directoryFile);
        }
    }

    [Fact]
    public void SharedPersistentDirectory_ReusesAndRelocatesOwnerAcrossIndependentInstances()
    {
        var directoryFile = CreateDirectoryFilePath();
        try
        {
            var membershipView = new StubClusterMembershipView(
                new Dictionary<string, NodeHealthStatus>
                {
                    ["node-a"] = NodeHealthStatus.Healthy,
                    ["node-b"] = NodeHealthStatus.Healthy,
                });
            var firstDirectory = CreateDirectory(directoryFile, membershipView, preferredNodeName: "node-a", relocationNodeName: "node-b");
            var secondDirectory = CreateDirectory(directoryFile, membershipView, preferredNodeName: "node-b", relocationNodeName: "node-b");
            var grainId = new GrainId("Echo", "shared");

            var first = firstDirectory.Resolve(grainId);
            var reused = secondDirectory.Resolve(grainId);

            membershipView.SetHealth("node-a", NodeHealthStatus.Unhealthy);
            var relocated = secondDirectory.Resolve(grainId);
            var observed = firstDirectory.Resolve(grainId);

            Assert.Equal("node-a", first.OwnerNodeName);
            Assert.Equal(first, reused);
            Assert.Equal(new GrainOwnerRecord(grainId, "node-b", Version: 2), relocated);
            Assert.Equal(relocated, observed);
        }
        finally
        {
            DeleteDirectoryArtifacts(directoryFile);
        }
    }

    [Fact]
    public void DirectoryGrainLocator_RefreshesCachedOwnerAfterSharedDirectoryVersionChanges()
    {
        var directoryFile = CreateDirectoryFilePath();
        try
        {
            var membershipView = new StubClusterMembershipView(
                new Dictionary<string, NodeHealthStatus>
                {
                    ["node-a"] = NodeHealthStatus.Healthy,
                    ["node-b"] = NodeHealthStatus.Healthy,
                });
            var firstDirectory = CreateDirectory(directoryFile, membershipView, preferredNodeName: "node-a", relocationNodeName: "node-b");
            var secondDirectory = CreateDirectory(directoryFile, membershipView, preferredNodeName: "node-b", relocationNodeName: "node-b");
            var locator = new DirectoryGrainLocator(firstDirectory);
            var grainId = new GrainId("Echo", "cache-refresh");

            var initial = locator.Locate(grainId);
            var updated = secondDirectory.SetOwner(grainId, "node-b");
            var refreshed = locator.Locate(grainId);

            Assert.Equal(new GrainAddress("node-a", grainId, OwnerVersion: 1), initial);
            Assert.Equal(new GrainOwnerRecord(grainId, "node-b", Version: 2), updated);
            Assert.Equal(new GrainAddress("node-b", grainId, OwnerVersion: 2), refreshed);
        }
        finally
        {
            DeleteDirectoryArtifacts(directoryFile);
        }
    }

    [Fact]
    public void DirectoryGrainLocator_InvalidatingOneGrainDoesNotReresolveOtherCachedEntries()
    {
        var directoryFile = CreateDirectoryFilePath();
        try
        {
            var membershipView = new StubClusterMembershipView(
                new Dictionary<string, NodeHealthStatus>
                {
                    ["node-a"] = NodeHealthStatus.Healthy,
                    ["node-b"] = NodeHealthStatus.Healthy,
                });
            var observedDirectory = new CountingGrainDirectory(CreateDirectory(
                directoryFile,
                membershipView,
                preferredNodeName: "node-a",
                relocationNodeName: "node-b"));
            var mutatingDirectory = CreateDirectory(
                directoryFile,
                membershipView,
                preferredNodeName: "node-b",
                relocationNodeName: "node-b");
            var locator = new DirectoryGrainLocator(observedDirectory);
            var grainA = new GrainId("Echo", "cache-invalidation-a");
            var grainB = new GrainId("Echo", "cache-invalidation-b");

            var initialA = locator.Locate(grainA);
            var initialB = locator.Locate(grainB);
            var updatedA = mutatingDirectory.SetOwner(grainA, "node-b");
            var cachedB = locator.Locate(grainB);
            var refreshedA = locator.Locate(grainA);

            Assert.Equal(new GrainAddress("node-a", grainA, OwnerVersion: 1), initialA);
            Assert.Equal(new GrainAddress("node-a", grainB, OwnerVersion: 1), initialB);
            Assert.Equal(new GrainOwnerRecord(grainA, "node-b", Version: 2), updatedA);
            Assert.Equal(initialB, cachedB);
            Assert.Equal(new GrainAddress("node-b", grainA, OwnerVersion: 2), refreshedA);
            Assert.Equal(2, observedDirectory.GetResolveCount(grainA));
            Assert.Equal(1, observedDirectory.GetResolveCount(grainB));
        }
        finally
        {
            DeleteDirectoryArtifacts(directoryFile);
        }
    }

    private static PersistentGrainDirectory CreateDirectory(
        string directoryFile,
        IClusterMembershipView membershipView,
        string preferredNodeName,
        string relocationNodeName)
        => new(
            new FileGrainDirectoryTable(directoryFile),
            membershipView,
            new StaticPlacementPolicy(preferredNodeName),
            new StaticLoadProvider(new PlacementLoadSnapshot([])),
            new StaticRelocationPolicy(relocationNodeName));

    private static string CreateDirectoryFilePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "orleans-replica-kernel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "grain-directory.json");
    }

    private static void DeleteDirectoryArtifacts(string directoryFile)
    {
        var directory = Path.GetDirectoryName(directoryFile);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class StaticPlacementPolicy : IPlacementPolicy
    {
        private readonly string _ownerNodeName;

        public StaticPlacementPolicy(string ownerNodeName)
        {
            _ownerNodeName = ownerNodeName;
        }

        public string SelectInitialOwner(
            GrainId grainId,
            IClusterMembershipView membershipView,
            PlacementLoadSnapshot loadSnapshot,
            GrainTypePlacementHint placementHint)
            => _ownerNodeName;
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

    private sealed class CountingGrainDirectory : IGrainDirectory
    {
        private readonly IGrainDirectory _inner;
        private readonly Dictionary<GrainId, int> _resolveCounts = new();

        public CountingGrainDirectory(IGrainDirectory inner)
        {
            _inner = inner;
        }

        public long GetInvalidationVersion() => _inner.GetInvalidationVersion();

        public long? GetRecordVersion(GrainId grainId) => _inner.GetRecordVersion(grainId);

        public GrainOwnerRecord Resolve(GrainId grainId, GrainInterfaceVersionDescriptor? requestedInterface = null)
        {
            _resolveCounts[grainId] = GetResolveCount(grainId) + 1;
            return _inner.Resolve(grainId, requestedInterface);
        }

        public GrainOwnerRecord SetOwner(GrainId grainId, string ownerNodeName)
            => _inner.SetOwner(grainId, ownerNodeName);

        public GrainDirectoryCheckpoint ExportCheckpoint() => _inner.ExportCheckpoint();

        public int GetResolveCount(GrainId grainId)
            => _resolveCounts.TryGetValue(grainId, out var count) ? count : 0;
    }
}
