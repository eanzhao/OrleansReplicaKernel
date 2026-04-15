using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Tests.TestSupport;

namespace OrleansReplicaKernel.Tests.Runtime;

public sealed class InProcessMembershipGossiperTests
{
    [Fact]
    public void Gossip_FanoutRotatesAcrossObservers_AndCheckpointPreservesCursor()
    {
        var baseline = RunFanoutCheckpointScenario();
        var resumed = RunFanoutCheckpointScenario();

        var baselineThird = baseline.Gossiper.Gossip();
        var restored = new InProcessMembershipGossiper(
            resumed.Membership,
            resumed.Views,
            fanout: 1,
            antiEntropyInterval: 5,
            resumed.Checkpoint);
        var resumedThird = restored.Gossip();

        Assert.Equal(baselineThird, resumedThird);
        Assert.Equal(
            baseline.Views[baselineThird[0].ObserverNodeName].GetHealth("node-c"),
            resumed.Views[resumedThird[0].ObserverNodeName].GetHealth("node-c"));
    }

    [Fact]
    public void Gossip_AntiEntropyRound_CatchesLaggingObserverAfterRestore()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        var membership = new InProcessClusterMembership(timeProvider);
        membership.Register("node-a");
        membership.Register("node-b");

        var views = CreateViews(timeProvider, "observer-a", "observer-b");
        var gossiper = new InProcessMembershipGossiper(
            membership,
            views,
            fanout: 1,
            antiEntropyInterval: 3);

        gossiper.Gossip();
        membership.SetHealth("node-b", NodeHealthStatus.Suspect, "probe timeout");

        var fanoutRound = gossiper.Gossip();
        Assert.Single(fanoutRound);

        var checkpoint = gossiper.ExportCheckpoint();
        var restored = new InProcessMembershipGossiper(
            membership,
            views,
            fanout: 1,
            antiEntropyInterval: 3,
            checkpoint);

        var antiEntropyRound = restored.Gossip();

        Assert.Single(antiEntropyRound);
        Assert.Equal("anti-entropy", antiEntropyRound[0].Mode);
        Assert.Equal("observer-b", antiEntropyRound[0].ObserverNodeName);
        Assert.Equal(NodeHealthStatus.Suspect, views["observer-b"].GetHealth("node-b"));
    }

    [Fact]
    public void Gossip_Restore_ContinuesFanoutAndStabilizationUsingConfiguredTimeProvider()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        var membership = new InProcessClusterMembership(timeProvider);
        membership.Register("node-a");
        membership.Register("node-b");

        var views = CreateViews(timeProvider, TimeSpan.FromSeconds(5), "observer-a", "observer-b");
        var gossiper = new InProcessMembershipGossiper(
            membership,
            views,
            fanout: 1,
            antiEntropyInterval: 10);

        var first = gossiper.Gossip();
        Assert.Equal(2, first.Count);
        Assert.All(first, delivery => Assert.Equal("anti-entropy", delivery.Mode));

        membership.SetHealth("node-b", NodeHealthStatus.Suspect, "probe timeout");
        var second = gossiper.Gossip();

        Assert.Single(second);
        Assert.Equal("fanout", second[0].Mode);
        Assert.Equal("observer-a", second[0].ObserverNodeName);
        Assert.Equal(NodeHealthStatus.Healthy, views["observer-a"].GetHealth("node-b"));
        Assert.Equal(NodeHealthStatus.Suspect, views["observer-a"].GetMembers().Single(member => member.NodeName == "node-b").ObservedStatus);

        var restored = new InProcessMembershipGossiper(
            membership,
            views,
            fanout: 1,
            antiEntropyInterval: 10,
            gossiper.ExportCheckpoint());

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var third = restored.Gossip();

        Assert.Equal(2, third.Count);
        Assert.Contains(
            third,
            delivery => delivery.ObserverNodeName == "observer-b"
                && delivery.Mode == "fanout"
                && delivery.ConsumedChanges == 1
                && delivery.StabilizedNodes == 1);
        Assert.Contains(
            third,
            delivery => delivery.ObserverNodeName == "observer-a"
                && delivery.Mode == "stabilization"
                && delivery.ConsumedChanges == 0
                && delivery.StabilizedNodes == 1);
        Assert.Equal(NodeHealthStatus.Suspect, views["observer-a"].GetHealth("node-b"));
        Assert.Equal(NodeHealthStatus.Suspect, views["observer-b"].GetHealth("node-b"));
    }

    private static Dictionary<string, GossipedClusterMembershipView> CreateViews(
        TimeProvider timeProvider,
        params string[] observerNodeNames)
        => CreateViews(timeProvider, TimeSpan.Zero, observerNodeNames);

    private static Dictionary<string, GossipedClusterMembershipView> CreateViews(
        TimeProvider timeProvider,
        TimeSpan stabilizationWindow,
        params string[] observerNodeNames)
        => observerNodeNames.ToDictionary(
            observerNodeName => observerNodeName,
            observerNodeName => new GossipedClusterMembershipView(
                observerNodeName,
                stabilizationWindow,
                timeProvider),
            StringComparer.Ordinal);

    private static (
        InProcessClusterMembership Membership,
        Dictionary<string, GossipedClusterMembershipView> Views,
        InProcessMembershipGossiper Gossiper,
        MembershipDisseminationCheckpoint Checkpoint)
        RunFanoutCheckpointScenario()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        var membership = new InProcessClusterMembership(timeProvider);
        membership.Register("node-a");
        membership.Register("node-b");
        membership.Register("node-c");

        var views = CreateViews(timeProvider, "observer-a", "observer-b", "observer-c");
        var gossiper = new InProcessMembershipGossiper(
            membership,
            views,
            fanout: 1,
            antiEntropyInterval: 5);

        var first = gossiper.Gossip();
        Assert.Equal(3, first.Count);
        Assert.All(first, delivery => Assert.Equal("anti-entropy", delivery.Mode));

        membership.SetHealth("node-c", NodeHealthStatus.Suspect, "probe timeout");

        var second = gossiper.Gossip();
        Assert.Single(second);
        Assert.Equal("fanout", second[0].Mode);

        return (membership, views, gossiper, gossiper.ExportCheckpoint());
    }
}
