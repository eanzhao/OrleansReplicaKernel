using OrleansReplicaKernel.App;

namespace OrleansReplicaKernel.Runtime;

public sealed class ConsecutiveFailureDetector : IFailureDetector
{
    private readonly object _lock = new();
    private readonly IClusterMembership _membership;
    private readonly Dictionary<string, int> _failureStates = new(StringComparer.Ordinal);

    public ConsecutiveFailureDetector(
        IClusterMembership membership)
    {
        _membership = membership;
    }

    public void ReportFailure(string nodeName, string reason)
    {
        if (!_membership.IsMember(nodeName))
        {
            return;
        }

        var count = 0;
        lock (_lock)
        {
            _failureStates.TryGetValue(nodeName, out count);
            count++;
            _failureStates[nodeName] = count;
        }

        if (count == 1)
        {
            TraceLog.Write(
                "failure-detector",
                $"signal failure {nodeName} count={count} -> {NodeHealthStatus.Suspect} reason={reason}");
            _membership.SetHealth(nodeName, NodeHealthStatus.Suspect, $"failure detector: {reason}");
            return;
        }

        TraceLog.Write(
            "failure-detector",
            $"signal failure {nodeName} count={count} -> {NodeHealthStatus.Unhealthy} reason={reason}");
        _membership.SetHealth(nodeName, NodeHealthStatus.Unhealthy, $"failure detector: {reason}");
    }

    public void ReportSuccess(string nodeName, string reason)
    {
        if (!_membership.IsMember(nodeName))
        {
            return;
        }

        lock (_lock)
        {
            _failureStates.Remove(nodeName);
        }

        TraceLog.Write("failure-detector", $"signal success {nodeName} reason={reason}");
        _membership.SetHealth(nodeName, NodeHealthStatus.Healthy, $"failure detector recovered: {reason}");
    }
}
