using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Tests.TestSupport;

namespace OrleansReplicaKernel.Tests.Runtime;

public sealed class GossipedClusterMembershipViewTests
{
    [Fact]
    public void ApplyGossip_DelaysStableStatusUntilWindowExpires()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 08, 0, 0, 0, TimeSpan.Zero));
        var view = new GossipedClusterMembershipView("observer-a", TimeSpan.FromSeconds(30), timeProvider);

        var initial = view.ApplyGossip(
            [
                new MembershipViewChange(1, "node-a", null, NodeHealthStatus.Healthy, "register", timeProvider.GetUtcNow()),
            ]);

        Assert.Equal(1, initial.ConsumedChanges);
        Assert.Equal(NodeHealthStatus.Healthy, view.GetHealth("node-a"));

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var suspect = view.ApplyGossip(
            [
                new MembershipViewChange(2, "node-a", NodeHealthStatus.Healthy, NodeHealthStatus.Suspect, "probe timeout", timeProvider.GetUtcNow()),
            ]);

        Assert.Equal(1, suspect.ConsumedChanges);
        Assert.Equal(0, suspect.StabilizedNodes);
        Assert.Equal(NodeHealthStatus.Healthy, view.GetHealth("node-a"));
        Assert.Equal(NodeHealthStatus.Suspect, view.GetMembers().Single().ObservedStatus);

        timeProvider.Advance(TimeSpan.FromSeconds(30));
        var tick = view.RunStabilizationTick();

        Assert.Equal(1, tick.StabilizedNodes);
        Assert.Equal(NodeHealthStatus.Suspect, view.GetHealth("node-a"));
    }

    [Fact]
    public void ExportCheckpointAndRestore_PreserveEpochAndObservedMembers()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 08, 0, 0, 0, TimeSpan.Zero));
        var view = new GossipedClusterMembershipView("observer-a", TimeSpan.FromSeconds(5), timeProvider);

        view.ApplyGossip(
            [
                new MembershipViewChange(1, "node-a", null, NodeHealthStatus.Healthy, "register", timeProvider.GetUtcNow()),
                new MembershipViewChange(2, "node-b", null, NodeHealthStatus.Healthy, "register", timeProvider.GetUtcNow()),
            ]);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        view.ApplyGossip(
            [
                new MembershipViewChange(3, "node-b", NodeHealthStatus.Healthy, NodeHealthStatus.Unhealthy, "probe failed", timeProvider.GetUtcNow()),
            ]);

        var restored = GossipedClusterMembershipView.Restore(view.ExportCheckpoint(), TimeSpan.FromSeconds(5), timeProvider);

        Assert.Equal(3, restored.CurrentEpoch);
        Assert.Equal(view.GetMembers(), restored.GetMembers());
    }
}
