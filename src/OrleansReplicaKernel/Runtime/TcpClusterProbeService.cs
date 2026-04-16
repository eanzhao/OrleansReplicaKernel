using System.Net;
using System.Net.Sockets;
using OrleansReplicaKernel.App;

namespace OrleansReplicaKernel.Runtime;

public sealed class TcpClusterProbeService : IClusterProbeService
{
    private readonly string _localNodeName;
    private readonly IClusterMembership _membership;
    private readonly IReadOnlyDictionary<string, IPEndPoint> _nodeEndpoints;
    private readonly IFailureDetector _failureDetector;

    public TcpClusterProbeService(
        string localNodeName,
        IClusterMembership membership,
        IReadOnlyDictionary<string, IPEndPoint> nodeEndpoints,
        IFailureDetector failureDetector)
    {
        _localNodeName = localNodeName;
        _membership = membership;
        _nodeEndpoints = nodeEndpoints;
        _failureDetector = failureDetector;
    }

    public async ValueTask<int> ProbePeersAsync(CancellationToken cancellationToken = default)
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

            if (!_nodeEndpoints.TryGetValue(member.NodeName, out var endpoint))
            {
                var missingEndpointReason = $"probe {_localNodeName} -> {member.NodeName} missed: endpoint is not configured";
                TraceLog.Write("probe", missingEndpointReason);
                _failureDetector.ReportFailure(member.NodeName, "probe endpoint is not configured");
                continue;
            }

            try
            {
                using var probeClient = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
                await probeClient.ConnectAsync(endpoint, timeout.Token);
                TraceLog.Write("probe", $"probe {_localNodeName} -> {member.NodeName} ack");
                _failureDetector.ReportSuccess(member.NodeName, $"probe ack from {_localNodeName}");
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                TraceLog.Write("probe", $"probe {_localNodeName} -> {member.NodeName} missed: {exception.GetType().Name}");
                _failureDetector.ReportFailure(member.NodeName, $"probe missed from {_localNodeName}");
            }
        }

        return probed;
    }
}
