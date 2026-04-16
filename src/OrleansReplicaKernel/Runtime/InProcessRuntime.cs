using System.Runtime.ExceptionServices;
using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Reminders;
using OrleansReplicaKernel.Routing;
using OrleansReplicaKernel.Streaming;
using OrleansReplicaKernel.Transactions;

namespace OrleansReplicaKernel.Runtime;

public sealed class InProcessRuntime : IObjectReferenceRuntime, IMessageReceiver, IResponseReceiver, IAsyncDisposable, IReminderRuntimeContext, IStreamRuntimeContext
{
    private const int MaxAttempts = 4;

    private readonly object _requestLock = new();
    private readonly object _responseLock = new();
    private readonly IFailureDetector _failureDetector;
    private readonly IGrainLocator _locator;
    private readonly IGrainRouter _router;
    private readonly IActivationDirectory _activationDirectory;
    private readonly IMessageTransport _transport;
    private readonly ObjectReferenceFactoryRegistry _objectReferences;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _responseHistoryRetention;
    private readonly InvocationSourceKind _sourceKind;
    private LocalReminderService? _reminderService;
    private IGrainStreamRuntime? _streamRuntime;
    private readonly Dictionary<Guid, CompletedRequestEntry> _completedRequests = new();
    private readonly Dictionary<Guid, Task<InvocationResponseMessage>> _inflightRequests = new();
    private readonly Dictionary<Guid, PendingResponseRegistration> _pendingResponses = new();
    private readonly Dictionary<Guid, SourceRequestState> _sourceRequests = new();

    private int _acceptedResponses;
    private int _lateResponses;
    private int _staleResponses;
    private int _duplicateResponses;

    public InProcessRuntime(
        string nodeName,
        IFailureDetector failureDetector,
        IGrainLocator locator,
        IGrainRouter router,
        IActivationDirectory activationDirectory,
        IMessageTransport transport,
        ObjectReferenceFactoryRegistry objectReferences,
        TimeProvider? timeProvider = null,
        TimeSpan? responseHistoryRetention = null,
        InvocationSourceKind sourceKind = InvocationSourceKind.ClusterNode)
    {
        NodeName = nodeName;
        _failureDetector = failureDetector;
        _locator = locator;
        _router = router;
        _activationDirectory = activationDirectory;
        _transport = transport;
        _objectReferences = objectReferences;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _responseHistoryRetention = responseHistoryRetention ?? TimeSpan.FromMinutes(5);
        _sourceKind = sourceKind;
    }

    public string NodeName { get; }

    public ObjectReferenceFactoryRegistry ObjectReferences => _objectReferences;

    internal void BindReminderService(LocalReminderService reminderService)
    {
        _reminderService = reminderService ?? throw new ArgumentNullException(nameof(reminderService));
    }

    internal void BindStreamRuntime(IGrainStreamRuntime streamRuntime)
    {
        _streamRuntime = streamRuntime ?? throw new ArgumentNullException(nameof(streamRuntime));
    }

    public ResponseDispositionSnapshot GetResponseDispositionSnapshot()
    {
        var utcNow = _timeProvider.GetUtcNow();
        var pendingResponses = 0;
        var trackedSourceRequests = 0;
        var completedTargetRequests = 0;

        lock (_responseLock)
        {
            EvictExpiredSourceStateLocked(utcNow);
            pendingResponses = _pendingResponses.Count;
            trackedSourceRequests = _sourceRequests.Count;
        }

        lock (_requestLock)
        {
            EvictExpiredCompletedRequestsLocked(utcNow);
            completedTargetRequests = _completedRequests.Count;
        }

        return new ResponseDispositionSnapshot(
            AcceptedResponses: Volatile.Read(ref _acceptedResponses),
            LateResponses: Volatile.Read(ref _lateResponses),
            StaleResponses: Volatile.Read(ref _staleResponses),
            DuplicateResponses: Volatile.Read(ref _duplicateResponses),
            PendingResponses: pendingResponses,
            TrackedSourceRequests: trackedSourceRequests,
            CompletedTargetRequests: completedTargetRequests);
    }

    public async ValueTask<TResult> InvokeAsync<TResult>(
        GrainId grainId,
        IInvokable invokable,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid();

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var attemptId = Guid.NewGuid();
            var routedMessage = CreateRoutedMessage(requestId, attemptId, attempt, grainId, invokable);
            var completion = RegisterPendingResponse(requestId, attemptId, attempt);

            try
            {
                _ = DispatchAsync(routedMessage);

                var response = await completion.Task.WaitAsync(cancellationToken);
                TraceLog.Write(
                    "response",
                    $"complete {response.RequestId:N}/{response.AttemptId:N}/#{response.AttemptSequence} from {response.ResponderNodeName}");

                if (response.Error is not null)
                {
                    if (response.Error is ActivationQuiescingException && attempt < MaxAttempts)
                    {
                        TraceLog.Write(
                            "retry",
                            $"activation quiescing for {grainId} via {routedMessage.Target.NodeName}, invalidate and retry request {requestId:N}");
                        _locator.Invalidate(grainId);
                        await Task.Delay(TimeSpan.FromMilliseconds(25), _timeProvider, cancellationToken);
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
                        $"response failure {response.RequestId:N}/{response.AttemptId:N}/#{response.AttemptSequence} from {response.ResponderNodeName}: {response.Error.GetType().Name}");
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
                RemovePendingResponse(attemptId, requestId, attempt, stopWaiting: true);
                if (routedMessage.Target.NodeName != NodeName)
                {
                    _failureDetector.ReportFailure(
                        routedMessage.Target.NodeName,
                        "request timed out");
                }

                TraceLog.Write(
                    "failure",
                    $"request {requestId:N}/{attemptId:N}/#{attempt} timed out or was canceled");
                throw;
            }
            catch (RemoteNodeUnavailableException exception) when (attempt < MaxAttempts)
            {
                RemovePendingResponse(attemptId, requestId, attempt, stopWaiting: false);
                _failureDetector.ReportFailure(exception.NodeName, "remote node unavailable");
                TraceLog.Write(
                    "retry",
                    $"node unavailable for {grainId} via {exception.NodeName}, invalidate and retry request {requestId:N}");
                _locator.Invalidate(grainId);
            }
            catch (ResponseDeliveryException exception) when (attempt < MaxAttempts)
            {
                RemovePendingResponse(attemptId, requestId, attempt, stopWaiting: false);
                _failureDetector.ReportFailure(
                    exception.NodeName,
                    $"response delivery failed: {exception.Reason}");
                TraceLog.Write(
                    "retry",
                    $"response delivery failed for {grainId} via {exception.NodeName}: {exception.Reason}; retry request {requestId:N}");
                await Task.Delay(TimeSpan.FromMilliseconds(25), _timeProvider, cancellationToken);
            }
            catch
            {
                RemovePendingResponse(attemptId, requestId, attempt, stopWaiting: true);
                throw;
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
            $"receive request {message.RequestId:N}/{message.AttemptId:N}/#{message.AttemptSequence} on {NodeName} from {message.SourceNodeName}");

        Task<InvocationResponseMessage>? requestTask = null;
        InvocationResponseMessage? completedResponse = null;
        var joinedInflight = false;

        lock (_requestLock)
        {
            EvictExpiredCompletedRequestsLocked(_timeProvider.GetUtcNow());

            if (_completedRequests.TryGetValue(message.RequestId, out var completed))
            {
                TraceLog.Write(
                    "dedupe",
                    $"replay cached response {message.RequestId:N} on {NodeName} for {message.Target.GrainId}");
                completedResponse = completed.Response with
                {
                    AttemptId = message.AttemptId,
                    AttemptSequence = message.AttemptSequence
                };
            }
            else if (_inflightRequests.TryGetValue(message.RequestId, out requestTask))
            {
                TraceLog.Write(
                    "dedupe",
                    $"join in-flight request {message.RequestId:N} on {NodeName} for {message.Target.GrainId}");
                joinedInflight = true;
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

        var response = await requestTask;
        return joinedInflight
            ? response with
            {
                AttemptId = message.AttemptId,
                AttemptSequence = message.AttemptSequence
            }
            : response;
    }

    public ValueTask ReceiveResponseAsync(
        InvocationResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        PendingResponseRegistration? pendingRegistration;
        string? discardCategory = null;
        string? discardReason = null;

        lock (_responseLock)
        {
            var utcNow = _timeProvider.GetUtcNow();
            EvictExpiredSourceStateLocked(utcNow);

            if (_pendingResponses.Remove(response.AttemptId, out pendingRegistration))
            {
                var state = _sourceRequests[response.RequestId];
                var pendingAttemptCount = Math.Max(0, state.PendingAttemptCount - 1);
                if (response.AttemptSequence < state.LatestAttemptSequence)
                {
                    discardCategory = "stale";
                    discardReason = $"attempt #{response.AttemptSequence} lost to newer attempt #{state.LatestAttemptSequence}";
                    _sourceRequests[response.RequestId] = state with
                    {
                        PendingAttemptCount = pendingAttemptCount,
                        UpdatedUtc = utcNow
                    };
                }
                else
                {
                    _sourceRequests[response.RequestId] = state with
                    {
                        CompletedAttemptSequence = response.AttemptSequence,
                        PendingAttemptCount = pendingAttemptCount,
                        WaitingStopped = false,
                        UpdatedUtc = utcNow
                    };
                }
            }
            else
            {
                ClassifyUnexpectedResponse(response, out discardCategory, out discardReason);
            }
        }

        if (discardCategory is not null || pendingRegistration is null)
        {
            RecordDiscardedResponse(discardCategory ?? "late", response, discardReason ?? "no pending completion");
            return ValueTask.CompletedTask;
        }

        Interlocked.Increment(ref _acceptedResponses);
        pendingRegistration.Completion.TrySetResult(response);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _activationDirectory.DisposeAsync();

    IGrainReminderRegistry? IReminderRuntimeContext.GetReminderRegistry(GrainId grainId)
        => _reminderService?.BindToGrain(grainId);

    IGrainStreamRuntime? IStreamRuntimeContext.GetStreamRuntime() => _streamRuntime;

    private InvocationMessage CreateRoutedMessage(
        Guid requestId,
        Guid attemptId,
        int attemptSequence,
        GrainId grainId,
        IInvokable invokable)
    {
        var message = new InvocationMessage(
            requestId,
            ActivationExecutionContext.CurrentRequestChainId ?? requestId,
            attemptId,
            attemptSequence,
            NodeName,
            new GrainAddress(NodeName, grainId, OwnerVersion: 0),
            invokable,
            _sourceKind,
            TransactionContext.Current);
        var routedAddress = _router.Route(message);
        var routedMessage = message with { Target = routedAddress };

        TraceLog.Write("proxy", $"pack {invokable.InterfaceName}.{invokable.MethodName} -> {grainId}");
        TraceLog.Write(
            "message",
            $"create {routedMessage.RequestId:N}/{routedMessage.AttemptId:N}/#{routedMessage.AttemptSequence} {routedMessage.SourceNodeName} -> {routedMessage.Target}");
        return routedMessage;
    }

    private async Task DispatchAsync(InvocationMessage message)
    {
        try
        {
            if (message.Target.NodeName == NodeName)
            {
                var localResponse = await ReceiveAsync(message, CancellationToken.None);
                await ReceiveResponseAsync(localResponse, CancellationToken.None);
                return;
            }

            await _transport.SendAsync(message, CancellationToken.None);
        }
        catch (Exception exception)
        {
            FailPendingResponse(message.AttemptId, message.RequestId, message.AttemptSequence, exception);
        }
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
            response = new InvocationResponseMessage(
                message.RequestId,
                message.AttemptId,
                message.AttemptSequence,
                NodeName,
                null,
                exception);
        }

        lock (_requestLock)
        {
            _inflightRequests.Remove(message.RequestId);
            _completedRequests[message.RequestId] = new CompletedRequestEntry(
                response,
                _timeProvider.GetUtcNow());
        }

        return response;
    }

    private async ValueTask<InvocationResponseMessage> DispatchLocalAsync(
        InvocationMessage message,
        CancellationToken cancellationToken)
    {
        var routedMessage = ResolveInboundTarget(message);
        if (routedMessage.Target.NodeName != NodeName)
        {
            return await ForwardAsync(routedMessage, cancellationToken);
        }

        try
        {
            var activation = _activationDirectory.GetOrCreate(routedMessage.Target);
            var result = await activation.InvokeAsync(routedMessage, this, cancellationToken);
            return new InvocationResponseMessage(
                routedMessage.RequestId,
                routedMessage.AttemptId,
                routedMessage.AttemptSequence,
                NodeName,
                result,
                null);
        }
        catch (ActivationInitializationException exception)
        {
            await CleanupFailedActivationAsync(routedMessage.Target, exception);
            return new InvocationResponseMessage(
                routedMessage.RequestId,
                routedMessage.AttemptId,
                routedMessage.AttemptSequence,
                NodeName,
                null,
                exception);
        }
        catch (Exception exception)
        {
            return new InvocationResponseMessage(
                routedMessage.RequestId,
                routedMessage.AttemptId,
                routedMessage.AttemptSequence,
                NodeName,
                null,
                exception);
        }
    }

    private InvocationMessage ResolveInboundTarget(InvocationMessage message)
    {
        if (CallbackTargetIdentity.TryGetExecutionNodeName(message.Target.GrainId, out var callbackExecutionNodeName)
            && !string.Equals(callbackExecutionNodeName, NodeName, StringComparison.Ordinal))
        {
            var forwardedCallback = message with
            {
                Target = message.Target with { NodeName = callbackExecutionNodeName }
            };

            TraceLog.Write(
                "gateway",
                $"forward callback {message.RequestId:N}/{message.AttemptId:N} {message.Target.GrainId} via {NodeName} -> {callbackExecutionNodeName}");
            return forwardedCallback;
        }

        if (message.SourceKind != InvocationSourceKind.Client)
        {
            return message;
        }

        var routedAddress = _router.Route(message);
        if (routedAddress.NodeName == message.Target.NodeName
            && routedAddress.OwnerVersion == message.Target.OwnerVersion)
        {
            return message;
        }

        TraceLog.Write(
            "gateway",
            $"forward client request {message.RequestId:N}/{message.AttemptId:N} {message.Target.GrainId} via {NodeName} -> {routedAddress}");
        return message with { Target = routedAddress };
    }

    private async ValueTask<InvocationResponseMessage> ForwardAsync(
        InvocationMessage message,
        CancellationToken cancellationToken)
    {
        var completion = RegisterPendingResponse(
            message.RequestId,
            message.AttemptId,
            message.AttemptSequence);

        try
        {
            await _transport.SendAsync(message, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        catch
        {
            RemovePendingResponse(
                message.AttemptId,
                message.RequestId,
                message.AttemptSequence,
                stopWaiting: true);
            throw;
        }
    }

    private async ValueTask CleanupFailedActivationAsync(
        GrainAddress address,
        ActivationInitializationException exception)
    {
        try
        {
            var removed = await _activationDirectory.DeactivateAsync(
                address,
                ActivationDeactivationReason.ActivationFailed);
            TraceLog.Write(
                "activation",
                $"cleanup failed activation {address.GrainId} on {address.NodeName} removed={removed}: {exception.Message}");
        }
        catch (Exception cleanupException)
        {
            TraceLog.Write(
                "activation",
                $"cleanup failed activation {address.GrainId} on {address.NodeName} errored: {cleanupException.Message}");
        }
    }

    private TaskCompletionSource<InvocationResponseMessage> RegisterPendingResponse(
        Guid requestId,
        Guid attemptId,
        int attemptSequence)
    {
        var completion = new TaskCompletionSource<InvocationResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_responseLock)
        {
            var utcNow = _timeProvider.GetUtcNow();
            EvictExpiredSourceStateLocked(utcNow);

            _sourceRequests[requestId] = _sourceRequests.TryGetValue(requestId, out var state)
                ? state with
                {
                    LatestAttemptSequence = Math.Max(state.LatestAttemptSequence, attemptSequence),
                    PendingAttemptCount = state.PendingAttemptCount + 1,
                    WaitingStopped = false,
                    UpdatedUtc = utcNow
                }
                : new SourceRequestState(
                    LatestAttemptSequence: attemptSequence,
                    CompletedAttemptSequence: null,
                    PendingAttemptCount: 1,
                    WaitingStopped: false,
                    UpdatedUtc: utcNow);
            _pendingResponses.Add(
                attemptId,
                new PendingResponseRegistration(requestId, attemptSequence, completion));
        }

        return completion;
    }

    private void RemovePendingResponse(
        Guid attemptId,
        Guid requestId,
        int attemptSequence,
        bool stopWaiting)
    {
        lock (_responseLock)
        {
            _pendingResponses.Remove(attemptId);
            var utcNow = _timeProvider.GetUtcNow();
            EvictExpiredSourceStateLocked(utcNow);

            if (!_sourceRequests.TryGetValue(requestId, out var state))
            {
                return;
            }

            var pendingAttemptCount = Math.Max(0, state.PendingAttemptCount - 1);
            if (attemptSequence < state.LatestAttemptSequence)
            {
                _sourceRequests[requestId] = state with
                {
                    PendingAttemptCount = pendingAttemptCount,
                    UpdatedUtc = utcNow
                };
                return;
            }

            _sourceRequests[requestId] = state with
            {
                PendingAttemptCount = pendingAttemptCount,
                WaitingStopped = stopWaiting,
                UpdatedUtc = utcNow
            };
        }
    }

    private void FailPendingResponse(
        Guid attemptId,
        Guid requestId,
        int attemptSequence,
        Exception exception)
    {
        PendingResponseRegistration? pendingRegistration;

        lock (_responseLock)
        {
            if (!_pendingResponses.Remove(attemptId, out pendingRegistration))
            {
                return;
            }

            var utcNow = _timeProvider.GetUtcNow();
            EvictExpiredSourceStateLocked(utcNow);

            if (_sourceRequests.TryGetValue(requestId, out var state)
                && attemptSequence >= state.LatestAttemptSequence)
            {
                _sourceRequests[requestId] = state with
                {
                    PendingAttemptCount = Math.Max(0, state.PendingAttemptCount - 1),
                    WaitingStopped = true,
                    UpdatedUtc = utcNow
                };
            }
            else if (_sourceRequests.TryGetValue(requestId, out var staleState))
            {
                _sourceRequests[requestId] = staleState with
                {
                    PendingAttemptCount = Math.Max(0, staleState.PendingAttemptCount - 1),
                    UpdatedUtc = utcNow
                };
            }
        }

        pendingRegistration.Completion.TrySetException(exception);
    }

    private void ClassifyUnexpectedResponse(
        InvocationResponseMessage response,
        out string category,
        out string reason)
    {
        if (!_sourceRequests.TryGetValue(response.RequestId, out var state))
        {
            category = "late";
            reason = "request is no longer tracked on the source node";
            return;
        }

        if (state.CompletedAttemptSequence is { } completedAttemptSequence)
        {
            if (response.AttemptSequence < completedAttemptSequence)
            {
                category = "stale";
                reason = $"completed by newer attempt #{completedAttemptSequence}";
                return;
            }

            if (response.AttemptSequence == completedAttemptSequence)
            {
                category = "duplicate";
                reason = $"attempt #{response.AttemptSequence} already completed";
                return;
            }

            category = "stale";
            reason = $"request already completed at attempt #{completedAttemptSequence}";
            return;
        }

        if (response.AttemptSequence < state.LatestAttemptSequence)
        {
            category = "stale";
            reason = $"source has already advanced to attempt #{state.LatestAttemptSequence}";
            return;
        }

        category = "late";
        reason = state.WaitingStopped
            ? "caller has already stopped waiting for this attempt"
            : "no pending completion exists for this attempt";
    }

    private void RecordDiscardedResponse(
        string category,
        InvocationResponseMessage response,
        string reason)
    {
        switch (category)
        {
            case "duplicate":
                Interlocked.Increment(ref _duplicateResponses);
                break;
            case "stale":
                Interlocked.Increment(ref _staleResponses);
                break;
            default:
                category = "late";
                Interlocked.Increment(ref _lateResponses);
                break;
        }

        TraceLog.Write(
            "response",
            $"discard {category} response {response.RequestId:N}/{response.AttemptId:N}/#{response.AttemptSequence} from {response.ResponderNodeName}: {reason}");
    }

    private void EvictExpiredCompletedRequestsLocked(DateTimeOffset utcNow)
    {
        if (_responseHistoryRetention == TimeSpan.Zero)
        {
            _completedRequests.Clear();
            return;
        }

        var cutoff = utcNow - _responseHistoryRetention;
        EvictFromDictionaryLocked(_completedRequests, entry => entry.CompletedUtc <= cutoff);
    }

    private void EvictExpiredSourceStateLocked(DateTimeOffset utcNow)
    {
        if (_responseHistoryRetention == TimeSpan.Zero)
        {
            EvictFromDictionaryLocked(_sourceRequests, state => state.PendingAttemptCount == 0);
            return;
        }

        var cutoff = utcNow - _responseHistoryRetention;
        EvictFromDictionaryLocked(_sourceRequests, state =>
            state.PendingAttemptCount == 0
            && state.UpdatedUtc <= cutoff
            && (state.CompletedAttemptSequence is not null || state.WaitingStopped));
    }

    private static void EvictFromDictionaryLocked<TKey, TValue>(
        Dictionary<TKey, TValue> dictionary,
        Func<TValue, bool> predicate)
        where TKey : notnull
    {
        List<TKey>? keysToRemove = null;
        foreach (var pair in dictionary)
        {
            if (!predicate(pair.Value)) continue;
            keysToRemove ??= [];
            keysToRemove.Add(pair.Key);
        }

        if (keysToRemove is null) return;
        foreach (var key in keysToRemove)
        {
            dictionary.Remove(key);
        }
    }

    private sealed record PendingResponseRegistration(
        Guid RequestId,
        int AttemptSequence,
        TaskCompletionSource<InvocationResponseMessage> Completion);

    private sealed record CompletedRequestEntry(
        InvocationResponseMessage Response,
        DateTimeOffset CompletedUtc);

    private sealed record SourceRequestState(
        int LatestAttemptSequence,
        int? CompletedAttemptSequence,
        int PendingAttemptCount,
        bool WaitingStopped,
        DateTimeOffset UpdatedUtc);
}
