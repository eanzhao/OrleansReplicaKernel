using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Serialization;

namespace OrleansReplicaKernel.Streaming;

internal sealed class MemoryStreamProvider : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly MemoryStreamProviderConfiguration _configuration;
    private readonly BinarySerializer _serializer;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<StreamId, StreamState> _streams = new();
    private readonly HashSet<(StreamId StreamId, Guid SubscriptionId)> _inflight = [];
    private readonly Dictionary<(StreamId, Guid), DeliveryFailureState> _failures = new();
    private readonly Task _loop;
    private IInvocationRuntime? _runtime;
    private ImplicitStreamSubscriptionRegistry? _implicitRegistry;

    public MemoryStreamProvider(
        MemoryStreamProviderConfiguration configuration,
        BinarySerializer serializer,
        TimeProvider timeProvider)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _loop = Task.Run(RunAsync);
    }

    public string ProviderName => _configuration.ProviderName;

    public void Bind(IInvocationRuntime runtime, ImplicitStreamSubscriptionRegistry? implicitRegistry = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _implicitRegistry = implicitRegistry;
    }

    public IAsyncStream<T> GetStream<T>(StreamId streamId)
        => new MemoryAsyncStream<T>(streamId, this);

    public async ValueTask PublishAsync<T>(
        StreamId streamId,
        T item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        lock (_lock)
        {
            var streamState = GetOrCreateStreamStateLocked(streamId, typeof(T));
            streamState.Events.Add(
                new StreamEventEnvelope(
                    streamState.NextSequenceToken,
                    _serializer.Serialize(item)));
            streamState.NextSequenceToken++;
        }

        await Task.Yield();
    }

    public async ValueTask<StreamSubscriptionState> SubscribeAsync<T>(
        StreamId streamId,
        GrainId subscriberGrainId,
        CancellationToken cancellationToken)
    {
        var initialSequenceToken = 1L;

        lock (_lock)
        {
            var streamState = GetOrCreateStreamStateLocked(streamId, typeof(T));
            initialSequenceToken = streamState.NextSequenceToken;
        }

        return await GetPubSubReference(streamId).RegisterSubscriptionAsync(
            subscriberGrainId,
            typeof(T).AssemblyQualifiedName ?? typeof(T).FullName ?? typeof(T).Name,
            initialSequenceToken,
            _timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public ValueTask<bool> UnsubscribeAsync(
        StreamId streamId,
        GrainId subscriberGrainId,
        CancellationToken cancellationToken)
        => new(GetPubSubReference(streamId).UnregisterSubscriptionAsync(subscriberGrainId, cancellationToken));

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        try
        {
            await _loop;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await ScanAsync(_cts.Token);
                await Task.Delay(_configuration.DispatchInterval, _timeProvider, _cts.Token);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var streamIds = Array.Empty<StreamId>();
        lock (_lock)
        {
            if (_streams.Count > 0)
            {
                streamIds = _streams.Keys.ToArray();
            }
        }

        foreach (var streamId in streamIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await EnsureImplicitSubscriptionsAsync(streamId, cancellationToken);

            var subscriptions = await GetPubSubReference(streamId).GetSubscriptionsAsync(cancellationToken);
            foreach (var subscription in subscriptions)
            {
                if (!TryMarkInflight(streamId, subscription.SubscriptionId))
                {
                    continue;
                }

                if (ShouldBackoff(streamId, subscription.SubscriptionId))
                {
                    ClearInflight(streamId, subscription.SubscriptionId);
                    continue;
                }

                StreamBatchEnvelope? batch = null;
                lock (_lock)
                {
                    if (_streams.TryGetValue(streamId, out var streamState))
                    {
                        batch = streamState.CreateBatch(
                            streamId,
                            subscription.NextSequenceToken,
                            _configuration.MaxBatchSize);
                    }
                }

                if (batch is null)
                {
                    ClearInflight(streamId, subscription.SubscriptionId);
                    continue;
                }

                _ = DeliverAsync(streamId, subscription, batch, cancellationToken);
            }
        }
    }

    private async Task EnsureImplicitSubscriptionsAsync(StreamId streamId, CancellationToken cancellationToken)
    {
        if (_implicitRegistry is null) return;

        var implicitGrainIds = _implicitRegistry.GetImplicitSubscribers(streamId);
        if (implicitGrainIds.Count == 0) return;

        string payloadTypeName;
        long initialSequenceToken;
        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out var streamState)) return;
            payloadTypeName = streamState.PayloadTypeName;
            initialSequenceToken = 1;
        }

        var pubSub = GetPubSubReference(streamId);
        foreach (var grainId in implicitGrainIds)
        {
            await pubSub.RegisterSubscriptionAsync(
                grainId,
                payloadTypeName,
                initialSequenceToken,
                _timeProvider.GetUtcNow(),
                cancellationToken);
        }
    }

    private async Task DeliverAsync(
        StreamId streamId,
        StreamSubscriptionState subscription,
        StreamBatchEnvelope batch,
        CancellationToken cancellationToken)
    {
        try
        {
            var runtime = _runtime
                ?? throw new InvalidOperationException($"Stream provider '{ProviderName}' has not been bound to a runtime.");
            await runtime.InvokeAsync<object?>(
                subscription.SubscriberGrainId,
                new StreamBatchDeliveryInvokable(batch),
                cancellationToken);
            await GetPubSubReference(streamId).CommitBatchAsync(
                subscription.SubscriptionId,
                subscription.NextSequenceToken,
                batch.NextSequenceToken,
                _timeProvider.GetUtcNow(),
                cancellationToken);
            lock (_lock)
            {
                if (_streams.TryGetValue(streamId, out var streamState))
                {
                    streamState.PruneEventsUpTo(batch.NextSequenceToken);
                }

                _failures.Remove((streamId, subscription.SubscriptionId));
            }

            TraceLog.Write(
                "stream",
                $"deliver {streamId} -> {subscription.SubscriberGrainId} range={batch.StartSequenceToken}-{batch.NextSequenceToken - 1} count={batch.Events.Count}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var failureKey = (streamId, subscription.SubscriptionId);
            lock (_lock)
            {
                if (!_failures.TryGetValue(failureKey, out var state))
                {
                    state = new DeliveryFailureState();
                    _failures[failureKey] = state;
                }

                state.ConsecutiveFailures++;
                state.NextRetryUtc = _timeProvider.GetUtcNow().Add(
                    ComputeBackoff(state.ConsecutiveFailures));

                if (state.ConsecutiveFailures >= _configuration.MaxDeliveryAttempts)
                {
                    state.DeadLetterEvents.AddRange(batch.Events);
                    _failures.Remove(failureKey);

                    _ = GetPubSubReference(streamId).CommitBatchAsync(
                        subscription.SubscriptionId,
                        subscription.NextSequenceToken,
                        batch.NextSequenceToken,
                        _timeProvider.GetUtcNow(),
                        cancellationToken);

                    TraceLog.Write(
                        "stream",
                        $"dead-letter {streamId} -> {subscription.SubscriberGrainId}: {batch.Events.Count} events after {_configuration.MaxDeliveryAttempts} failures");
                }
                else
                {
                    TraceLog.Write(
                        "stream",
                        $"deliver failed {streamId} -> {subscription.SubscriberGrainId} (attempt {state.ConsecutiveFailures}/{_configuration.MaxDeliveryAttempts}): {exception.Message}");
                }
            }
        }
        finally
        {
            ClearInflight(streamId, subscription.SubscriptionId);
        }
    }

    private bool ShouldBackoff(StreamId streamId, Guid subscriptionId)
    {
        lock (_lock)
        {
            if (_failures.TryGetValue((streamId, subscriptionId), out var state)
                && state.NextRetryUtc > _timeProvider.GetUtcNow())
            {
                return true;
            }
        }

        return false;
    }

    private static TimeSpan ComputeBackoff(int consecutiveFailures)
    {
        var delayMs = Math.Min(100 * Math.Pow(2, consecutiveFailures - 1), 30_000);
        return TimeSpan.FromMilliseconds(delayMs);
    }

    public IReadOnlyList<StreamEventEnvelope> GetDeadLetterEvents(StreamId streamId, Guid subscriptionId)
    {
        lock (_lock)
        {
            return _failures.TryGetValue((streamId, subscriptionId), out var state)
                ? state.DeadLetterEvents.ToArray()
                : [];
        }
    }

    private bool TryMarkInflight(StreamId streamId, Guid subscriptionId)
    {
        lock (_lock)
        {
            return _inflight.Add((streamId, subscriptionId));
        }
    }

    private void ClearInflight(StreamId streamId, Guid subscriptionId)
    {
        lock (_lock)
        {
            _inflight.Remove((streamId, subscriptionId));
        }
    }

    private PubSubRendezvousGrainReference GetPubSubReference(StreamId streamId)
    {
        var runtime = _runtime
            ?? throw new InvalidOperationException($"Stream provider '{ProviderName}' has not been bound to a runtime.");
        return new PubSubRendezvousGrainReference(
            runtime,
            new GrainId(StreamingGrainTypes.PubSubRendezvous, streamId.ToStableKey()));
    }

    private StreamState GetOrCreateStreamStateLocked(StreamId streamId, Type payloadType)
    {
        if (_streams.TryGetValue(streamId, out var existing))
        {
            if (!string.Equals(existing.PayloadTypeName, payloadType.AssemblyQualifiedName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Stream '{streamId}' is already bound to payload '{existing.PayloadTypeName}', not '{payloadType.AssemblyQualifiedName}'.");
            }

            return existing;
        }

        var created = new StreamState(payloadType.AssemblyQualifiedName ?? payloadType.FullName ?? payloadType.Name);
        _streams.Add(streamId, created);
        return created;
    }

    private sealed class StreamState
    {
        public StreamState(string payloadTypeName)
        {
            PayloadTypeName = payloadTypeName;
        }

        public string PayloadTypeName { get; }

        public long NextSequenceToken { get; set; } = 1;

        public List<StreamEventEnvelope> Events { get; } = [];

        public void PruneEventsUpTo(long sequenceToken)
        {
            Events.RemoveAll(item => item.SequenceToken < sequenceToken);
        }

        public StreamBatchEnvelope? CreateBatch(
            StreamId streamId,
            long nextSequenceToken,
            int maxBatchSize)
        {
            if (nextSequenceToken <= 0 || Events.Count == 0)
            {
                return null;
            }

            var items = Events
                .Where(item => item.SequenceToken >= nextSequenceToken)
                .Take(maxBatchSize)
                .ToArray();
            if (items.Length == 0)
            {
                return null;
            }

            return new StreamBatchEnvelope(
                streamId,
                PayloadTypeName,
                items[0].SequenceToken,
                items[^1].SequenceToken + 1,
                items);
        }
    }

    private sealed class DeliveryFailureState
    {
        public int ConsecutiveFailures { get; set; }
        public DateTimeOffset NextRetryUtc { get; set; }
        public List<StreamEventEnvelope> DeadLetterEvents { get; } = [];
    }

    private sealed class MemoryAsyncStream<T> : IAsyncStream<T>
    {
        private readonly StreamId _streamId;
        private readonly MemoryStreamProvider _owner;

        public MemoryAsyncStream(
            StreamId streamId,
            MemoryStreamProvider owner)
        {
            _streamId = streamId;
            _owner = owner;
        }

        public ValueTask OnNextAsync(T item, CancellationToken cancellationToken = default)
            => _owner.PublishAsync(_streamId, item, cancellationToken);

        public ValueTask<StreamSubscriptionState> SubscribeAsync(CancellationToken cancellationToken = default)
        {
            var grainId = Runtime.ActivationExecutionContext.CurrentGrainId
                ?? throw new InvalidOperationException("Stream subscription requires an active grain execution context.");
            return _owner.SubscribeAsync<T>(_streamId, grainId, cancellationToken);
        }

        public ValueTask<bool> UnsubscribeAsync(CancellationToken cancellationToken = default)
        {
            var grainId = Runtime.ActivationExecutionContext.CurrentGrainId
                ?? throw new InvalidOperationException("Stream unsubscription requires an active grain execution context.");
            return _owner.UnsubscribeAsync(_streamId, grainId, cancellationToken);
        }
    }
}
