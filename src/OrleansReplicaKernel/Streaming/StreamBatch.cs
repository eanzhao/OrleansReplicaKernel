namespace OrleansReplicaKernel.Streaming;

public sealed class StreamBatch<T>
{
    public StreamBatch(
        StreamId streamId,
        long startSequenceToken,
        long endSequenceToken,
        IReadOnlyList<T> items)
    {
        StreamId = streamId;
        StartSequenceToken = startSequenceToken;
        EndSequenceToken = endSequenceToken;
        Items = items ?? throw new ArgumentNullException(nameof(items));
    }

    public StreamId StreamId { get; }

    public long StartSequenceToken { get; }

    public long EndSequenceToken { get; }

    public IReadOnlyList<T> Items { get; }
}
