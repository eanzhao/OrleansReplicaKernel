using OrleansReplicaKernel.App;

namespace OrleansReplicaKernel.Runtime;

public sealed class InProcessClusterProbeService : IClusterProbeService
{
    private readonly string _localNodeName;
    private readonly IClusterMembership _membership;
    private readonly InProcessNodeRegistry _nodeRegistry;
    private readonly IFailureDetector _failureDetector;

    public InProcessClusterProbeService(
        string localNodeName,
        IClusterMembership membership,
        InProcessNodeRegistry nodeRegistry,
        IFailureDetector failureDetector)
    {
        _localNodeName = localNodeName;
        _membership = membership;
        _nodeRegistry = nodeRegistry;
        _failureDetector = failureDetector;
    }

    public ValueTask<int> ProbePeersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var probed = 0;
        foreach (var member in _membership.GetMembers())
        {
            if (string.Equals(member.NodeName, _localNodeName, StringComparison.Ordinal))
            {
                continue;
            }

            probed++;

            if (_nodeRegistry.Probe(_localNodeName, member.NodeName, out var detail))
            {
                TraceLog.Write("probe", detail);
                _failureDetector.ReportSuccess(member.NodeName, $"probe ack from {_localNodeName}");
                continue;
            }

            TraceLog.Write("probe", detail);
            _failureDetector.ReportFailure(member.NodeName, $"probe missed from {_localNodeName}");
        }

        return ValueTask.FromResult(probed);
    }
}
