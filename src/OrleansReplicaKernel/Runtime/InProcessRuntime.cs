using System.Runtime.ExceptionServices;
using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Routing;

namespace OrleansReplicaKernel.Runtime;

public sealed class InProcessRuntime : IInvocationRuntime, IMessageReceiver, IAsyncDisposable
{
    private const int MaxAttempts = 4;

    private readonly object _requestLock = new();
    private readonly IFailureDetector _failureDetector;
    private readonly IGrainLocator _locator;
    private readonly IGrainRouter _router;
    private readonly IActivationDirectory _activationDirectory;
    private readonly IMessageTransport _transport;
    private readonly Dictionary<Guid, InvocationResponseMessage> _completedRequests = new();
    private readonly Dictionary<Guid, Task<InvocationResponseMessage>> _inflightRequests = new();

    public InProcessRuntime(
        string nodeName,
        IFailureDetector failureDetector,
        IGrainLocator locator,
        IGrainRouter router,
        IActivationDirectory activationDirectory,
        IMessageTransport transport)
    {
        NodeName = nodeName;
        _failureDetector = failureDetector;
        _locator = locator;
        _router = router;
        _activationDirectory = activationDirectory;
        _transport = transport;
    }

    public string NodeName { get; }

    public async ValueTask<TResult> InvokeAsync<TResult>(
        GrainId grainId,
        IInvokable invokable,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid();

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var routedMessage = CreateRoutedMessage(requestId, grainId, invokable);

            try
            {
                var response = await DispatchAsync(routedMessage, cancellationToken);
                TraceLog.Write("response", $"complete {response.RequestId:N} from {response.ResponderNodeName}");

                if (response.Error is not null)
                {
                    if (response.Error is ActivationQuiescingException && attempt < MaxAttempts)
                    {
                        TraceLog.Write(
                            "retry",
                            $"activation quiescing for {grainId} via {routedMessage.Target.NodeName}, invalidate and retry request {requestId:N}");
                        _locator.Invalidate(grainId);
                        await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
                        continue;
                    }

                    if (response.Error is StaleGrainAddressException staleAddress && attempt < MaxAttempts)
                    {
                        TraceLog.Write(
                            "retry",
                            $"stale grain address for {grainId} via {staleAddress.NodeName}: request v{staleAddress.MessageOwnerVersion}, fenced v{staleAddress.FencedOwnerVersion}; invalidate and retry request {requestId:N}");
                        _locator.Invalidate(grainId);
                        continue;
                    }

                    TraceLog.Write(
                        "failure",
                        $"response failure {response.RequestId:N} from {response.ResponderNodeName}: {response.Error.GetType().Name}");
                    if (routedMessage.Target.NodeName != NodeName)
                    {
                        _failureDetector.ReportSuccess(
                            routedMessage.Target.NodeName,
                            "response arrived with remote execution error");
                    }
                    ExceptionDispatchInfo.Capture(response.Error).Throw();
                }

                if (routedMessage.Target.NodeName != NodeName)
                {
                    _failureDetector.ReportSuccess(
                        routedMessage.Target.NodeName,
                        "remote response arrived");
                }

                return (TResult)response.Result!;
            }
            catch (OperationCanceledException)
            {
                if (routedMessage.Target.NodeName != NodeName)
                {
                    _failureDetector.ReportFailure(
                        routedMessage.Target.NodeName,
                        "request timed out");
                }
                TraceLog.Write("failure", $"request {requestId:N} timed out or was canceled");
                throw;
            }
            catch (RemoteNodeUnavailableException exception) when (attempt < MaxAttempts)
            {
                _failureDetector.ReportFailure(exception.NodeName, "remote node unavailable");
                TraceLog.Write(
                    "retry",
                    $"node unavailable for {grainId} via {exception.NodeName}, invalidate and retry request {requestId:N}");
                _locator.Invalidate(grainId);
            }
            catch (ResponseDeliveryException exception) when (attempt < MaxAttempts)
            {
                _failureDetector.ReportFailure(exception.NodeName, $"response delivery failed: {exception.Reason}");
                TraceLog.Write(
                    "retry",
                    $"response delivery failed for {grainId} via {exception.NodeName}: {exception.Reason}; retry request {requestId:N}");
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }

        throw new InvalidOperationException($"Request for grain '{grainId}' exhausted its retry budget.");
    }

    public async ValueTask<InvocationResponseMessage> ReceiveAsync(
        InvocationMessage message,
        CancellationToken cancellationToken = default)
    {
        TraceLog.Write(
            "runtime",
            $"receive request {message.RequestId:N} on {NodeName} from {message.SourceNodeName}");

        Task<InvocationResponseMessage>? requestTask = null;
        InvocationResponseMessage? completedResponse = null;

        lock (_requestLock)
        {
            if (_completedRequests.TryGetValue(message.RequestId, out var completed))
            {
                TraceLog.Write(
                    "dedupe",
                    $"replay cached response {message.RequestId:N} on {NodeName} for {message.Target.GrainId}");
                completedResponse = completed;
            }
            else if (_inflightRequests.TryGetValue(message.RequestId, out requestTask))
            {
                TraceLog.Write(
                    "dedupe",
                    $"join in-flight request {message.RequestId:N} on {NodeName} for {message.Target.GrainId}");
            }
            else
            {
                requestTask = ProcessIncomingRequestAsync(message, cancellationToken);
                _inflightRequests[message.RequestId] = requestTask;
            }
        }

        if (completedResponse is not null)
        {
            return completedResponse;
        }

        if (requestTask is null)
        {
            throw new InvalidOperationException(
                $"No request task was created for request '{message.RequestId:N}'.");
        }

        return await requestTask;
    }

    public ValueTask DisposeAsync() => _activationDirectory.DisposeAsync();

    private InvocationMessage CreateRoutedMessage(
        Guid requestId,
        GrainId grainId,
        IInvokable invokable)
    {
        var message = new InvocationMessage(
            requestId,
            NodeName,
            new GrainAddress(NodeName, grainId, OwnerVersion: 0),
            invokable);
        var routedAddress = _router.Route(message);
        var routedMessage = message with { Target = routedAddress };

        TraceLog.Write("proxy", $"pack {invokable.InterfaceName}.{invokable.MethodName} -> {grainId}");
        TraceLog.Write(
            "message",
            $"create {routedMessage.RequestId:N} {routedMessage.SourceNodeName} -> {routedMessage.Target}");
        return routedMessage;
    }

    private ValueTask<InvocationResponseMessage> DispatchAsync(
        InvocationMessage message,
        CancellationToken cancellationToken)
    {
        if (message.Target.NodeName == NodeName)
        {
            return ReceiveAsync(message, cancellationToken);
        }

        return _transport.SendAsync(message, cancellationToken);
    }

    private async Task<InvocationResponseMessage> ProcessIncomingRequestAsync(
        InvocationMessage message,
        CancellationToken cancellationToken)
    {
        InvocationResponseMessage response;

        try
        {
            response = await DispatchLocalAsync(message, cancellationToken);
        }
        catch (Exception exception)
        {
            response = new InvocationResponseMessage(message.RequestId, NodeName, null, exception);
        }

        lock (_requestLock)
        {
            _inflightRequests.Remove(message.RequestId);
            _completedRequests[message.RequestId] = response;
        }

        return response;
    }

    private async ValueTask<InvocationResponseMessage> DispatchLocalAsync(
        InvocationMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            var activation = _activationDirectory.GetOrCreate(message.Target);
            var result = await activation.InvokeAsync(message, cancellationToken);
            return new InvocationResponseMessage(message.RequestId, NodeName, result, null);
        }
        catch (Exception exception)
        {
            return new InvocationResponseMessage(message.RequestId, NodeName, null, exception);
        }
    }
}
