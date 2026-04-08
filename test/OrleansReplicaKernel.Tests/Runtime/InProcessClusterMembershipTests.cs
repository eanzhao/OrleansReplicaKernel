using OrleansReplicaKernel.Runtime;

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
}
