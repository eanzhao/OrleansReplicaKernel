namespace OrleansReplicaKernel.Streaming;

internal sealed record StreamEventEnvelope(
    long SequenceToken,
    byte[] Payload);

internal sealed record StreamBatchEnvelope(
    StreamId StreamId,
    string PayloadTypeName,
    long StartSequenceToken,
    long NextSequenceToken,
    IReadOnlyList<StreamEventEnvelope> Events);
