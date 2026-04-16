using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Routing;

namespace OrleansReplicaKernel.Tests.Routing;

public sealed class GatewayGrainLocatorTests
{
    [Fact]
    public void GatewayLocator_LoadBalancesAcrossConfiguredGateways_AndRotatesAfterInvalidation()
    {
        var selector = new RoundRobinGatewaySelector(["gateway-a", "gateway-b"]);
        var locator = new GatewayGrainLocator(selector);
        var grainA = new GrainId("Echo", "a");
        var grainB = new GrainId("Echo", "b");

        var first = locator.Locate(grainA);
        var second = locator.Locate(grainB);
        var repeated = locator.Locate(grainA);

        locator.Invalidate(grainA);
        var reassigned = locator.Locate(grainA);

        Assert.Equal(new GrainAddress("gateway-a", grainA, OwnerVersion: 0), first);
        Assert.Equal(new GrainAddress("gateway-b", grainB, OwnerVersion: 0), second);
        Assert.Equal(new GrainAddress("gateway-a", grainA, OwnerVersion: 0), repeated);
        Assert.Equal(new GrainAddress("gateway-b", grainA, OwnerVersion: 0), reassigned);
    }

    [Fact]
    public void CallbackTargetIdentity_CanSeparateRoutingNodeAndExecutionNode()
    {
        var grainId = CallbackTargetIdentity.Create("echo-observer", "gateway-a", "client-1");

        Assert.True(CallbackTargetIdentity.TryGetRoutingNodeName(grainId, out var routingNodeName));
        Assert.True(CallbackTargetIdentity.TryGetExecutionNodeName(grainId, out var executionNodeName));
        Assert.Equal("gateway-a", routingNodeName);
        Assert.Equal("client-1", executionNodeName);
    }
}
