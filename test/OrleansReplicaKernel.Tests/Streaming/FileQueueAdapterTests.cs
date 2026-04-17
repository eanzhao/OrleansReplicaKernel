using OrleansReplicaKernel.Streaming;

namespace OrleansReplicaKernel.Tests.Streaming;

public sealed class FileQueueAdapterTests : IDisposable
{
    private readonly string _directory;
    private readonly FileQueueAdapter _adapter;

    public FileQueueAdapterTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "OrleansReplicaKernel.Tests",
            "file-queue",
            Guid.NewGuid().ToString("N"));
        _adapter = new FileQueueAdapter("test-queue", _directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task EnqueueAndDequeue_SingleMessage_RoundTrips()
    {
        var streamId = new StreamId("provider", "ns", "key1");
        var payload = "hello"u8.ToArray();

        await _adapter.EnqueueAsync(streamId, "System.String", payload);

        var messages = await _adapter.DequeueAsync(10);

        Assert.Single(messages);
        var msg = messages[0];
        Assert.Equal(streamId, msg.StreamId);
        Assert.Equal("System.String", msg.PayloadTypeName);
        Assert.Equal(payload, msg.Payload);
        Assert.Equal(1, msg.SequenceToken);
    }

    [Fact]
    public async Task DequeueAsync_EmptyQueue_ReturnsEmpty()
    {
        var messages = await _adapter.DequeueAsync(10);

        Assert.Empty(messages);
    }

    [Fact]
    public async Task Dequeue_RespectsMaxCount()
    {
        var streamId = new StreamId("provider", "ns", "key1");

        await _adapter.EnqueueAsync(streamId, "System.String", "a"u8.ToArray());
        await _adapter.EnqueueAsync(streamId, "System.String", "b"u8.ToArray());
        await _adapter.EnqueueAsync(streamId, "System.String", "c"u8.ToArray());

        var messages = await _adapter.DequeueAsync(2);

        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public async Task Dequeue_PreservesOrdering()
    {
        var streamId = new StreamId("provider", "ns", "key1");

        await _adapter.EnqueueAsync(streamId, "System.String", "first"u8.ToArray());
        await _adapter.EnqueueAsync(streamId, "System.String", "second"u8.ToArray());
        await _adapter.EnqueueAsync(streamId, "System.String", "third"u8.ToArray());

        var messages = await _adapter.DequeueAsync(10);

        Assert.Equal(3, messages.Count);
        Assert.Equal(1, messages[0].SequenceToken);
        Assert.Equal(2, messages[1].SequenceToken);
        Assert.Equal(3, messages[2].SequenceToken);
        Assert.Equal("first"u8.ToArray(), messages[0].Payload);
        Assert.Equal("second"u8.ToArray(), messages[1].Payload);
        Assert.Equal("third"u8.ToArray(), messages[2].Payload);
    }

    [Fact]
    public async Task Acknowledge_RemovesMessage()
    {
        var streamId = new StreamId("provider", "ns", "key1");

        await _adapter.EnqueueAsync(streamId, "System.String", "a"u8.ToArray());
        await _adapter.EnqueueAsync(streamId, "System.String", "b"u8.ToArray());

        var messages = await _adapter.DequeueAsync(10);
        Assert.Equal(2, messages.Count);

        await _adapter.AcknowledgeAsync(messages[0].MessageId);

        var remaining = await _adapter.DequeueAsync(10);
        Assert.Single(remaining);
        Assert.Equal(messages[1].MessageId, remaining[0].MessageId);
    }

    [Fact]
    public async Task Acknowledge_NonExistentMessageId_DoesNotThrow()
    {
        await _adapter.AcknowledgeAsync("00000000000000000099");
    }

    [Fact]
    public async Task Enqueue_AssignsIncrementingSequenceTokens()
    {
        var streamId = new StreamId("provider", "ns", "key1");

        await _adapter.EnqueueAsync(streamId, "System.String", "a"u8.ToArray());
        await _adapter.EnqueueAsync(streamId, "System.String", "b"u8.ToArray());
        await _adapter.EnqueueAsync(streamId, "System.String", "c"u8.ToArray());

        var messages = await _adapter.DequeueAsync(10);

        Assert.Equal(1, messages[0].SequenceToken);
        Assert.Equal(2, messages[1].SequenceToken);
        Assert.Equal(3, messages[2].SequenceToken);
    }

    [Fact]
    public async Task Enqueue_MultipleStreams_KeepsSeparate()
    {
        var stream1 = new StreamId("provider", "ns1", "key1");
        var stream2 = new StreamId("provider", "ns2", "key2");

        await _adapter.EnqueueAsync(stream1, "System.String", "from-1"u8.ToArray());
        await _adapter.EnqueueAsync(stream2, "System.String", "from-2"u8.ToArray());

        var messages = await _adapter.DequeueAsync(10);

        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, m => m.StreamId == stream1);
        Assert.Contains(messages, m => m.StreamId == stream2);
    }

    [Fact]
    public async Task Enqueue_PreservesEnqueueTimestamp()
    {
        var streamId = new StreamId("provider", "ns", "key1");
        var beforeEnqueue = DateTimeOffset.UtcNow;

        await _adapter.EnqueueAsync(streamId, "System.String", "data"u8.ToArray());

        var messages = await _adapter.DequeueAsync(10);
        var afterDequeue = DateTimeOffset.UtcNow;

        Assert.InRange(messages[0].EnqueuedUtc, beforeEnqueue, afterDequeue);
    }

    [Fact]
    public void Constructor_ThrowsOnNullOrWhitespaceName()
    {
        Assert.Throws<ArgumentException>(() => new FileQueueAdapter("", _directory));
        Assert.Throws<ArgumentException>(() => new FileQueueAdapter(" ", _directory));
    }

    [Fact]
    public void Constructor_ThrowsOnNullOrWhitespaceDirectory()
    {
        Assert.Throws<ArgumentException>(() => new FileQueueAdapter("test", ""));
        Assert.Throws<ArgumentException>(() => new FileQueueAdapter("test", " "));
    }

    [Fact]
    public void Name_ReturnsConfiguredName()
    {
        Assert.Equal("test-queue", _adapter.Name);
    }
}
