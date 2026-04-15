using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Tests.TestSupport;

namespace OrleansReplicaKernel.Tests.Runtime;

public sealed class InProcessClusterMembershipTests
{
    [Fact]
    public void RegisterAndSetHealth_AdvanceEpochAndKeepViewChangesOrdered()
    {
        var membership = new InProcessClusterMembership();

        membership.Register("node-a");
        membership.Register("node-b");
        membership.SetHealth("node-a", NodeHealthStatus.Suspect, "probe timeout");
        membership.SetHealth("node-a", NodeHealthStatus.Unhealthy, "probe timeout again");

        Assert.Equal(4, membership.CurrentEpoch);
        Assert.Equal(new[] { "node-b" }, membership.GetHealthyMembers());

        var viewChanges = membership.GetViewChanges();
        Assert.Equal(4, viewChanges.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, viewChanges.Select(item => item.Epoch));
        Assert.Equal(NodeHealthStatus.Healthy, viewChanges[0].CurrentStatus);
        Assert.Equal(NodeHealthStatus.Unhealthy, viewChanges[^1].CurrentStatus);
    }

    [Fact]
    public void ViewChanges_UseConfiguredTimeProvider()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 15, 0, 0, 0, TimeSpan.Zero));
        var membership = new InProcessClusterMembership(timeProvider);

        membership.Register("node-a");
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        membership.SetHealth("node-a", NodeHealthStatus.Suspect, "probe timeout");
        timeProvider.Advance(TimeSpan.FromSeconds(7));
        membership.SetHealth("node-a", NodeHealthStatus.Unhealthy, "probe timeout again");

        var viewChanges = membership.GetViewChanges();

        Assert.Equal(new DateTimeOffset(2026, 04, 15, 0, 0, 0, TimeSpan.Zero), viewChanges[0].CreatedAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 04, 15, 0, 0, 5, TimeSpan.Zero), viewChanges[1].CreatedAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 04, 15, 0, 0, 12, TimeSpan.Zero), viewChanges[2].CreatedAtUtc);
    }

    [Fact]
    public void ExportCheckpointAndRestore_PreserveMembersEpochAndHistory()
    {
        var membership = new InProcessClusterMembership();
        membership.Register("node-a");
        membership.Register("node-b");
        membership.SetHealth("node-b", NodeHealthStatus.Suspect, "slow heartbeat");

        var restored = InProcessClusterMembership.Restore(membership.ExportCheckpoint());

        Assert.Equal(membership.CurrentEpoch, restored.CurrentEpoch);
        Assert.Equal(membership.GetMembers(), restored.GetMembers());
        Assert.Equal(
            membership.GetViewChangesSince(1).Select(item => item.Epoch),
            restored.GetViewChangesSince(1).Select(item => item.Epoch));
    }

    [Fact]
    public void Restore_ContinuesEmittingViewChangesUsingConfiguredTimeProvider()
    {
        var sourceTimeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 15, 0, 0, 0, TimeSpan.Zero));
        var membership = new InProcessClusterMembership(sourceTimeProvider);
        membership.Register("node-a");

        var restoreTimeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 3, 30, 0, TimeSpan.Zero));
        var restored = InProcessClusterMembership.Restore(membership.ExportCheckpoint(), restoreTimeProvider);

        restoreTimeProvider.Advance(TimeSpan.FromMinutes(2));
        restored.SetHealth("node-a", NodeHealthStatus.Suspect, "restored probe timeout");

        var lastViewChange = restored.GetViewChanges().Last();

        Assert.Equal(2, lastViewChange.Epoch);
        Assert.Equal(new DateTimeOffset(2026, 04, 16, 3, 32, 0, TimeSpan.Zero), lastViewChange.CreatedAtUtc);
    }
}
