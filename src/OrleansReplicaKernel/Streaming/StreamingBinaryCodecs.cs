using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Serialization;

namespace OrleansReplicaKernel.Streaming;

internal sealed class StreamIdBinaryCodec : BinaryObjectCodec<StreamId>
{
    public override string Alias => "orleans.stream.id";

    protected override StreamId ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        string? providerName = null;
        string? namespaceName = null;
        string? key = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            switch (fieldId)
            {
                case 1:
                    providerName = serializer.Read<string>(payload);
                    break;
                case 2:
                    namespaceName = serializer.Read<string>(payload);
                    break;
                case 3:
                    key = serializer.Read<string>(payload);
                    break;
            }
        }

        return new StreamId(
            BinaryCodecRequired.Require(providerName, nameof(StreamId.ProviderName)),
            BinaryCodecRequired.Require(namespaceName, nameof(StreamId.Namespace)),
            BinaryCodecRequired.Require(key, nameof(StreamId.Key)));
    }

    protected override void WriteFields(BinaryObjectWriter writer, StreamId value, BinarySerializer serializer)
    {
        writer.WriteField(1, value.ProviderName, serializer);
        writer.WriteField(2, value.Namespace, serializer);
        writer.WriteField(3, value.Key, serializer);
    }
}

internal sealed class StreamSubscriptionStateBinaryCodec : BinaryObjectCodec<StreamSubscriptionState>
{
    public override string Alias => "orleans.stream.subscription-state";

    protected override StreamSubscriptionState ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        Guid? subscriptionId = null;
        GrainId? subscriberGrainId = null;
        long? nextSequenceToken = null;
        DateTimeOffset? updatedUtc = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            switch (fieldId)
            {
                case 1:
                    subscriptionId = serializer.Read<Guid>(payload);
                    break;
                case 2:
                    subscriberGrainId = serializer.Read<GrainId>(payload);
                    break;
                case 3:
                    nextSequenceToken = serializer.Read<long>(payload);
                    break;
                case 4:
                    updatedUtc = serializer.Read<DateTimeOffset>(payload);
                    break;
            }
        }

        return new StreamSubscriptionState(
            BinaryCodecRequired.Require(subscriptionId, nameof(StreamSubscriptionState.SubscriptionId)),
            BinaryCodecRequired.Require(subscriberGrainId, nameof(StreamSubscriptionState.SubscriberGrainId)),
            BinaryCodecRequired.Require(nextSequenceToken, nameof(StreamSubscriptionState.NextSequenceToken)),
            BinaryCodecRequired.Require(updatedUtc, nameof(StreamSubscriptionState.UpdatedUtc)));
    }

    protected override void WriteFields(BinaryObjectWriter writer, StreamSubscriptionState value, BinarySerializer serializer)
    {
        writer.WriteField(1, value.SubscriptionId, serializer);
        writer.WriteField(2, value.SubscriberGrainId, serializer);
        writer.WriteField(3, value.NextSequenceToken, serializer);
        writer.WriteField(4, value.UpdatedUtc, serializer);
    }
}

internal sealed class StreamEventEnvelopeBinaryCodec : BinaryObjectCodec<StreamEventEnvelope>
{
    public override string Alias => "orleans.stream.event-envelope";

    protected override StreamEventEnvelope ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        long? sequenceToken = null;
        byte[]? payloadBytes = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            switch (fieldId)
            {
                case 1:
                    sequenceToken = serializer.Read<long>(payload);
                    break;
                case 2:
                    payloadBytes = serializer.Read<byte[]>(payload);
                    break;
            }
        }

        return new StreamEventEnvelope(
            BinaryCodecRequired.Require(sequenceToken, nameof(StreamEventEnvelope.SequenceToken)),
            BinaryCodecRequired.Require(payloadBytes, nameof(StreamEventEnvelope.Payload)));
    }

    protected override void WriteFields(BinaryObjectWriter writer, StreamEventEnvelope value, BinarySerializer serializer)
    {
        writer.WriteField(1, value.SequenceToken, serializer);
        writer.WriteField(2, value.Payload, serializer);
    }
}

internal sealed class StreamBatchEnvelopeBinaryCodec : BinaryObjectCodec<StreamBatchEnvelope>
{
    public override string Alias => "orleans.stream.batch-envelope";

    protected override StreamBatchEnvelope ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        StreamId? streamId = null;
        string? payloadTypeName = null;
        long? startSequenceToken = null;
        long? nextSequenceToken = null;
        List<StreamEventEnvelope>? events = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            switch (fieldId)
            {
                case 1:
                    streamId = serializer.Read<StreamId>(payload);
                    break;
                case 2:
                    payloadTypeName = serializer.Read<string>(payload);
                    break;
                case 3:
                    startSequenceToken = serializer.Read<long>(payload);
                    break;
                case 4:
                    nextSequenceToken = serializer.Read<long>(payload);
                    break;
                case 5:
                    events = serializer.Read<List<StreamEventEnvelope>>(payload);
                    break;
            }
        }

        return new StreamBatchEnvelope(
            BinaryCodecRequired.Require(streamId, nameof(StreamBatchEnvelope.StreamId)),
            BinaryCodecRequired.Require(payloadTypeName, nameof(StreamBatchEnvelope.PayloadTypeName)),
            BinaryCodecRequired.Require(startSequenceToken, nameof(StreamBatchEnvelope.StartSequenceToken)),
            BinaryCodecRequired.Require(nextSequenceToken, nameof(StreamBatchEnvelope.NextSequenceToken)),
            BinaryCodecRequired.Require(events, nameof(StreamBatchEnvelope.Events)));
    }

    protected override void WriteFields(BinaryObjectWriter writer, StreamBatchEnvelope value, BinarySerializer serializer)
    {
        writer.WriteField(1, value.StreamId, serializer);
        writer.WriteField(2, value.PayloadTypeName, serializer);
        writer.WriteField(3, value.StartSequenceToken, serializer);
        writer.WriteField(4, value.NextSequenceToken, serializer);
        writer.WriteField(5, value.Events.ToList(), serializer);
    }
}

internal sealed class StreamBatchDeliveryInvokableBinaryCodec : BinaryObjectCodec<StreamBatchDeliveryInvokable>
{
    public override string Alias => "orleans.stream.batch-delivery-invokable";

    protected override StreamBatchDeliveryInvokable ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        StreamBatchEnvelope? envelope = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            if (fieldId == 1)
            {
                envelope = serializer.Read<StreamBatchEnvelope>(payload);
            }
        }

        return new StreamBatchDeliveryInvokable(
            BinaryCodecRequired.Require(envelope, nameof(StreamBatchDeliveryInvokable.Envelope)));
    }

    protected override void WriteFields(BinaryObjectWriter writer, StreamBatchDeliveryInvokable value, BinarySerializer serializer)
    {
        writer.WriteField(1, value.Envelope, serializer);
    }
}
