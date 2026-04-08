using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Messaging;

namespace OrleansReplicaKernel.Runtime;

public sealed class InProcessMessageTransport : IMessageTransport
{
    private readonly IClusterMembershipView _membershipView;
    private readonly InProcessNodeRegistry _nodeRegistry;

    public InProcessMessageTransport(
        IClusterMembershipView membershipView,
        InProcessNodeRegistry nodeRegistry)
    {
        _membershipView = membershipView;
        _nodeRegistry = nodeRegistry;
    }

    public async ValueTask<InvocationResponseMessage> SendAsync(
        InvocationMessage message,
        CancellationToken cancellationToken = default)
    {
        if (_membershipView.GetHealth(message.Target.NodeName) == NodeHealthStatus.Unhealthy)
        {
            throw new RemoteNodeUnavailableException(message.Target.NodeName);
        }

        var dispatch = _nodeRegistry.PrepareDispatch(message.Target.NodeName);

        if (dispatch.Delay is { } delay && delay > TimeSpan.Zero)
        {
            TraceLog.Write(
                "transport",
                $"delay request {message.RequestId:N} to {message.Target.NodeName} by {delay}");
            await Task.Delay(delay, cancellationToken);
        }

        TraceLog.Write(
            "transport",
            $"forward request {message.RequestId:N} {message.SourceNodeName} -> {message.Target.NodeName}");

        if (dispatch.ResponseError is not null)
        {
            TraceLog.Write(
                "transport",
                $"inject failure response {message.RequestId:N} from {message.Target.NodeName}: {dispatch.ResponseError.GetType().Name}");
            return new InvocationResponseMessage(message.RequestId, message.Target.NodeName, null, dispatch.ResponseError);
        }

        var response = await dispatch.Receiver.ReceiveAsync(message, cancellationToken);

        TraceLog.Write(
            "transport",
            $"receive response {response.RequestId:N} from {response.ResponderNodeName}");

        return response;
    }
}
