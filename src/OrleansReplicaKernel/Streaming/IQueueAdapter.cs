namespace OrleansReplicaKernel.Streaming;

public interface IQueueAdapter
{
    string Name { get; }

    ValueTask EnqueueAsync(StreamId streamId, string payloadTypeName, byte[] payload, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<QueueMessage>> DequeueAsync(int maxCount, CancellationToken cancellationToken = default);

    ValueTask AcknowledgeAsync(string messageId, CancellationToken cancellationToken = default);
}

public sealed record QueueMessage(
    string MessageId,
    StreamId StreamId,
    string PayloadTypeName,
    byte[] Payload,
    long SequenceToken,
    DateTimeOffset EnqueuedUtc);
