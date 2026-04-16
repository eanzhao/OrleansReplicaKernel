using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using OrleansReplicaKernel.Serialization;

namespace OrleansReplicaKernel.Streaming;

internal sealed class GrainStreamRuntime : IGrainStreamRuntime, IStreamBatchDeliveryRuntime
{
    private static readonly ConcurrentDictionary<Type, Func<BinarySerializer, ReadOnlyMemory<byte>, object>> Deserializers = new();

    private readonly BinarySerializer _serializer;
    private readonly IReadOnlyDictionary<string, MemoryStreamProvider> _providers;

    public GrainStreamRuntime(
        BinarySerializer serializer,
        IReadOnlyDictionary<string, MemoryStreamProvider> providers)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    public IAsyncStream<T> GetStream<T>(string providerName, string namespaceName, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!_providers.TryGetValue(providerName, out var provider))
        {
            throw new InvalidOperationException($"No stream provider registered for '{providerName}'.");
        }

        return provider.GetStream<T>(new StreamId(providerName, namespaceName, key));
    }

    public async ValueTask DeliverBatchAsync(
        object target,
        StreamBatchEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(envelope);

        var observerInterface = target.GetType()
            .GetInterfaces()
            .FirstOrDefault(@interface =>
                @interface.IsGenericType
                && @interface.GetGenericTypeDefinition() == typeof(IAsyncStreamSubscriptionObserver<>)
                && string.Equals(
                    @interface.GetGenericArguments()[0].AssemblyQualifiedName,
                    envelope.PayloadTypeName,
                    StringComparison.Ordinal));
        if (observerInterface is null)
        {
            throw new InvalidOperationException(
                $"Target '{target.GetType().Name}' does not implement a stream observer for payload '{envelope.PayloadTypeName}'.");
        }

        var payloadType = observerInterface.GetGenericArguments()[0];
        var items = CreatePayloadList(payloadType, envelope.Events);
        var batchType = typeof(StreamBatch<>).MakeGenericType(payloadType);
        var batch = Activator.CreateInstance(
            batchType,
            envelope.StreamId,
            envelope.StartSequenceToken,
            envelope.NextSequenceToken - 1,
            items)
            ?? throw new InvalidOperationException($"Unable to create stream batch '{batchType.FullName}'.");
        var deliveryMethod = observerInterface.GetMethod(nameof(IAsyncStreamSubscriptionObserver<int>.OnNextBatchAsync))
            ?? throw new InvalidOperationException($"No batch delivery method found on '{observerInterface.FullName}'.");
        var deliveryTask = (ValueTask)deliveryMethod.Invoke(target, [batch, cancellationToken])!;
        await deliveryTask;
    }

    private object CreatePayloadList(Type payloadType, IReadOnlyList<StreamEventEnvelope> events)
    {
        var listType = typeof(List<>).MakeGenericType(payloadType);
        var list = (System.Collections.IList)(Activator.CreateInstance(listType)
            ?? throw new InvalidOperationException($"Unable to create payload list '{listType.FullName}'."));
        var deserialize = Deserializers.GetOrAdd(payloadType, static type =>
        {
            var serializerParameter = Expression.Parameter(typeof(BinarySerializer), "serializer");
            var payloadParameter = Expression.Parameter(typeof(ReadOnlyMemory<byte>), "payload");
            var method = typeof(BinarySerializer)
                .GetMethod(nameof(BinarySerializer.Deserialize))
                ?.MakeGenericMethod(type)
                ?? throw new InvalidOperationException("Unable to bind binary serializer deserialize method.");
            var call = Expression.Call(serializerParameter, method, payloadParameter);
            var body = Expression.Convert(call, typeof(object));
            return Expression.Lambda<Func<BinarySerializer, ReadOnlyMemory<byte>, object>>(
                    body,
                    serializerParameter,
                    payloadParameter)
                .Compile();
        });

        foreach (var item in events)
        {
            list.Add(deserialize(_serializer, item.Payload));
        }

        return list;
    }
}
