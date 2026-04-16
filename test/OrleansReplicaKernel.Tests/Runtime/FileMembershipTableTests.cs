using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Tests.TestSupport;

namespace OrleansReplicaKernel.Tests.Runtime;

public sealed class FileMembershipTableTests
{
    [Fact]
    public async Task UpdateAsync_RejectsStaleVersionWrites()
    {
        var membershipFile = CreateMembershipFilePath();
        try
        {
            var table = new FileMembershipTable(membershipFile);
            var initial = await table.ReadAsync();
            var registeredAt = new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero);
            var registeredCheckpoint = new ClusterMembershipCheckpoint(
                CurrentEpoch: 1,
                Members: [new ClusterMemberRecord("node-a", NodeHealthStatus.Healthy)],
                ViewChanges:
                [
                    new MembershipViewChange(
                        Epoch: 1,
                        NodeName: "node-a",
                        PreviousStatus: null,
                        CurrentStatus: NodeHealthStatus.Healthy,
                        Reason: "register",
                        CreatedAtUtc: registeredAt)
                ]);

            var registered = await table.RegisterAsync(new MembershipTableWriteRequest(initial.Version, registeredCheckpoint));
            var staleUpdate = await table.UpdateAsync(
                new MembershipTableWriteRequest(
                    initial.Version,
                    new ClusterMembershipCheckpoint(
                        CurrentEpoch: 2,
                        Members: [new ClusterMemberRecord("node-a", NodeHealthStatus.Unhealthy)],
                        ViewChanges:
                        [
                            .. registeredCheckpoint.ViewChanges,
                            new MembershipViewChange(
                                Epoch: 2,
                                NodeName: "node-a",
                                PreviousStatus: NodeHealthStatus.Healthy,
                                CurrentStatus: NodeHealthStatus.Unhealthy,
                                Reason: "stale update",
                                CreatedAtUtc: registeredAt.AddSeconds(5))
                        ])));
            var current = await table.ReadAsync();

            Assert.True(registered);
            Assert.False(staleUpdate);
            Assert.Equal(1, current.Version);
            Assert.Equal(registeredCheckpoint.CurrentEpoch, current.Checkpoint.CurrentEpoch);
            Assert.Equal(registeredCheckpoint.Members, current.Checkpoint.Members);
            Assert.Equal(registeredCheckpoint.ViewChanges, current.Checkpoint.ViewChanges);
        }
        finally
        {
            DeleteMembershipArtifacts(membershipFile);
        }
    }

    [Fact]
    public void SharedFileMembershipTable_AllowsIndependentMembershipInstancesToObserveEachOther()
    {
        var membershipFile = CreateMembershipFilePath();
        try
        {
            var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
            var firstMembership = new InProcessClusterMembership(new FileMembershipTable(membershipFile), timeProvider);
            var secondMembership = new InProcessClusterMembership(new FileMembershipTable(membershipFile), timeProvider);

            firstMembership.Register("node-a");
            Assert.True(secondMembership.IsMember("node-a"));

            timeProvider.Advance(TimeSpan.FromSeconds(5));
            secondMembership.Register("node-b");
            secondMembership.SetHealth("node-a", NodeHealthStatus.Suspect, "shared storage");

            Assert.Equal(new[] { "node-a", "node-b" }, firstMembership.GetMembers().Select(item => item.NodeName));
            Assert.Equal(NodeHealthStatus.Suspect, firstMembership.GetHealth("node-a"));
            Assert.Equal(3, firstMembership.CurrentEpoch);
            Assert.Equal(new long[] { 1, 2, 3 }, firstMembership.GetViewChanges().Select(item => item.Epoch));
        }
        finally
        {
            DeleteMembershipArtifacts(membershipFile);
        }
    }

    private static string CreateMembershipFilePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "orleans-replica-kernel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "membership.json");
    }

    private static void DeleteMembershipArtifacts(string membershipFile)
    {
        var directory = Path.GetDirectoryName(membershipFile);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
