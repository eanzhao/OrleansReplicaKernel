using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Routing;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Tests.TestSupport;

namespace OrleansReplicaKernel.Tests.Routing;

public sealed class PlacementPoliciesTests
{
    [Fact]
    public void LeastLoadedPlacementPolicy_PrefersConfiguredNodeWhenLoadIsTied()
    {
        var membershipView = new StubClusterMembershipView(
            new Dictionary<string, NodeHealthStatus>
            {
                ["node-a"] = NodeHealthStatus.Healthy,
                ["node-b"] = NodeHealthStatus.Healthy,
                ["node-c"] = NodeHealthStatus.Unhealthy,
            });
        var policy = new LeastLoadedPlacementPolicy("node-b");
        var snapshot = new PlacementLoadSnapshot(
            [
                new ActivationLoadRecord("node-a", 2),
                new ActivationLoadRecord("node-b", 2),
                new ActivationLoadRecord("node-c", 0),
            ]);

        var owner = policy.SelectInitialOwner(new GrainId("Echo", "1"), membershipView, snapshot);

        Assert.Equal("node-b", owner);
    }

    [Fact]
    public void LeastLoadedPlacementPolicy_ThrowsWhenNoHealthyNodeExists()
    {
        var membershipView = new StubClusterMembershipView(
            new Dictionary<string, NodeHealthStatus>
            {
                ["node-a"] = NodeHealthStatus.Unhealthy,
            });
        var policy = new LeastLoadedPlacementPolicy("node-a");

        var exception = Assert.Throws<InvalidOperationException>(
            () => policy.SelectInitialOwner(new GrainId("Echo", "1"), membershipView, new PlacementLoadSnapshot([])));

        Assert.Contains("No healthy placement candidate", exception.Message);
    }

    [Fact]
    public void HealthyNodeRelocationPolicy_UsesPreferredNodeFirstAndThenFallsBack()
    {
        var membershipView = new StubClusterMembershipView(
            new Dictionary<string, NodeHealthStatus>
            {
                ["node-a"] = NodeHealthStatus.Unhealthy,
                ["node-b"] = NodeHealthStatus.Healthy,
                ["node-c"] = NodeHealthStatus.Healthy,
            });
        var policy = new HealthyNodeRelocationPolicy("node-b");
        var grainId = new GrainId("Echo", "1");

        Assert.Equal("node-b", policy.SelectOwner(grainId, "node-a", membershipView));

        membershipView.SetHealth("node-b", NodeHealthStatus.Unhealthy);
        Assert.Equal("node-c", policy.SelectOwner(grainId, "node-a", membershipView));
    }

    [Fact]
    public void LoadSkewRebalancingPolicy_SelectsLeastLoadedHealthyNodeWhenSkewIsLargeEnough()
    {
        var membershipView = new StubClusterMembershipView(
            new Dictionary<string, NodeHealthStatus>
            {
                ["node-a"] = NodeHealthStatus.Healthy,
                ["node-b"] = NodeHealthStatus.Healthy,
                ["node-c"] = NodeHealthStatus.Healthy,
            });
        var policy = new LoadSkewRebalancingPolicy(minimumSkew: 5);
        var snapshot = new PlacementLoadSnapshot(
            [
                new ActivationLoadRecord("node-a", 10),
                new ActivationLoadRecord("node-b", 4),
                new ActivationLoadRecord("node-c", 6),
            ]);

        var target = policy.SelectHandoffTarget(
            new GrainOwnerRecord(new GrainId("Counter", "1"), "node-a", Version: 3),
            membershipView,
            snapshot);

        Assert.Equal("node-b", target);
    }

    [Fact]
    public void LoadSkewRebalancingPolicy_ReturnsNullWhenSkewIsTooSmall()
    {
        var membershipView = new StubClusterMembershipView(
            new Dictionary<string, NodeHealthStatus>
            {
                ["node-a"] = NodeHealthStatus.Healthy,
                ["node-b"] = NodeHealthStatus.Healthy,
            });
        var policy = new LoadSkewRebalancingPolicy(minimumSkew: 5);
        var snapshot = new PlacementLoadSnapshot(
            [
                new ActivationLoadRecord("node-a", 6),
                new ActivationLoadRecord("node-b", 3),
            ]);

        var target = policy.SelectHandoffTarget(
            new GrainOwnerRecord(new GrainId("Counter", "1"), "node-a", Version: 1),
            membershipView,
            snapshot);

        Assert.Null(target);
    }
}
