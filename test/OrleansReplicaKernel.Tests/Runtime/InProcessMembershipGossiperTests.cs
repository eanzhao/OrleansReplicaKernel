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

    private static Dictionary<string, GossipedClusterMembershipView> CreateViews(
        TimeProvider timeProvider,
        params string[] observerNodeNames)
        => observerNodeNames.ToDictionary(
            observerNodeName => observerNodeName,
            observerNodeName => new GossipedClusterMembershipView(
                observerNodeName,
                TimeSpan.Zero,
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
