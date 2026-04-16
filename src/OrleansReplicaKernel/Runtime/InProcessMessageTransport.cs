using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Serialization;

namespace OrleansReplicaKernel.Runtime;

public sealed class InProcessMessageTransport : IMessageTransport
{
    private readonly IClusterMembershipView _membershipView;
    private readonly BinaryMessageSerializer _messageSerializer;
    private readonly InProcessNodeRegistry _nodeRegistry;
    private readonly TimeProvider _timeProvider;

    public InProcessMessageTransport(
        IClusterMembershipView membershipView,
        InProcessNodeRegistry nodeRegistry,
        BinaryMessageSerializer messageSerializer,
        TimeProvider? timeProvider = null)
    {
        _membershipView = membershipView;
        _nodeRegistry = nodeRegistry;
        _messageSerializer = messageSerializer ?? throw new ArgumentNullException(nameof(messageSerializer));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask SendAsync(
        InvocationMessage message,
        CancellationToken cancellationToken = default)
    {
        if (_membershipView.GetHealth(message.Target.NodeName) == NodeHealthStatus.Unhealthy)
        {
            throw new RemoteNodeUnavailableException(message.Target.NodeName);
        }

        var dispatch = _nodeRegistry.PrepareDispatch(message.SourceNodeName, message.Target.NodeName);

        if (dispatch.Delay is { } delay && delay > TimeSpan.Zero)
        {
            TraceLog.Write(
                "transport",
                $"delay request {message.RequestId:N} to {message.Target.NodeName} by {delay}");
            await Task.Delay(delay, _timeProvider, CancellationToken.None);
        }

        TraceLog.Write(
            "transport",
            $"forward request {message.RequestId:N}/{message.AttemptId:N} {message.SourceNodeName} -> {message.Target.NodeName}");

        var serializedRequest = _messageSerializer.SerializeInvocationMessage(message);
        var wireRequest = _messageSerializer.DeserializeInvocationMessage(serializedRequest);

        if (dispatch.ResponseError is not null)
        {
            TraceLog.Write(
                "transport",
                $"inject failure response {message.RequestId:N} from {message.Target.NodeName}: {dispatch.ResponseError.GetType().Name}");
            var injectedFailure = new InvocationResponseMessage(
                    message.RequestId,
                    message.AttemptId,
                    message.AttemptSequence,
                    message.Target.NodeName,
                    null,
                    dispatch.ResponseError);
            var serializedFailure = _messageSerializer.SerializeInvocationResponse(injectedFailure);
            var wireFailure = _messageSerializer.DeserializeInvocationResponse(serializedFailure);
            await dispatch.ResponseReceiver.ReceiveResponseAsync(
                wireFailure,
                CancellationToken.None);
            return;
        }

        var response = await dispatch.RequestReceiver.ReceiveAsync(wireRequest, CancellationToken.None);

        if (dispatch.DroppedResponseReason is { } droppedResponseReason)
        {
            if (dispatch.DroppedResponseReplayDelay is { } replayDelay)
            {
                ReplayResponseLater(
                    dispatch.ResponseReceiver,
                    response,
                    replayDelay,
                    "replay dropped",
                    _timeProvider);
            }

            TraceLog.Write(
                "transport",
                $"drop response {response.RequestId:N}/{response.AttemptId:N} from {response.ResponderNodeName}: {droppedResponseReason}");
            throw new ResponseDeliveryException(response.ResponderNodeName, droppedResponseReason);
        }

        TraceLog.Write(
            "transport",
            $"receive response {response.RequestId:N}/{response.AttemptId:N} from {response.ResponderNodeName}");

        var serializedResponse = _messageSerializer.SerializeInvocationResponse(response);
        var wireResponse = _messageSerializer.DeserializeInvocationResponse(serializedResponse);

        await dispatch.ResponseReceiver.ReceiveResponseAsync(wireResponse, CancellationToken.None);

        if (dispatch.DuplicateResponseDelay is { } duplicateDelay)
        {
            ReplayResponseLater(
                dispatch.ResponseReceiver,
                wireResponse,
                duplicateDelay,
                "duplicate",
                _timeProvider);
        }
    }

    private static void ReplayResponseLater(
        IResponseReceiver responseReceiver,
        InvocationResponseMessage response,
        TimeSpan delay,
        string mode,
        TimeProvider timeProvider)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, timeProvider, CancellationToken.None);
                    }

                    TraceLog.Write(
                        "transport",
                        $"{mode} response {response.RequestId:N}/{response.AttemptId:N} from {response.ResponderNodeName} after {delay}");
                    await responseReceiver.ReceiveResponseAsync(response, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    TraceLog.Write(
                        "transport",
                        $"{mode} response {response.RequestId:N}/{response.AttemptId:N} failed: {exception.GetType().Name}: {exception.Message}");
                }
            });
    }
}
