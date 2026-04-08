using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Tests.Runtime;

public sealed class ConsecutiveFailureDetectorTests
{
    [Fact]
    public void ReportFailure_MarksMemberAsSuspectThenUnhealthy()
    {
        var membership = new InProcessClusterMembership();
        membership.Register("node-a");
        var detector = new ConsecutiveFailureDetector(membership);

        detector.ReportFailure("node-a", "probe timeout");
        Assert.Equal(NodeHealthStatus.Suspect, membership.GetHealth("node-a"));

        detector.ReportFailure("node-a", "probe timeout again");
        Assert.Equal(NodeHealthStatus.Unhealthy, membership.GetHealth("node-a"));
    }

    [Fact]
    public void ReportSuccess_ClearsFailureCountSoNextFailureStartsFromSuspect()
    {
        var membership = new InProcessClusterMembership();
        membership.Register("node-a");
        var detector = new ConsecutiveFailureDetector(membership);

        detector.ReportFailure("node-a", "first");
        detector.ReportFailure("node-a", "second");
        detector.ReportSuccess("node-a", "recovered");

        Assert.Equal(NodeHealthStatus.Healthy, membership.GetHealth("node-a"));

        detector.ReportFailure("node-a", "fresh failure");
        Assert.Equal(NodeHealthStatus.Suspect, membership.GetHealth("node-a"));
    }

    [Fact]
    public void ReportFailure_IgnoresUnknownMembers()
    {
        var membership = new InProcessClusterMembership();
        var detector = new ConsecutiveFailureDetector(membership);

        detector.ReportFailure("missing-node", "probe timeout");
        detector.ReportSuccess("missing-node", "probe recovered");

        Assert.Empty(membership.GetMembers());
    }
}
