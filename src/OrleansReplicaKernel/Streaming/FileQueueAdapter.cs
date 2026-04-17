using System.Text.Json;

namespace OrleansReplicaKernel.Streaming;

public sealed class FileQueueAdapter : IQueueAdapter
{
    private readonly string _directory;
    private readonly object _lock = new();
    private long _nextSequenceToken = 1;

    public FileQueueAdapter(string name, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Name = name;
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public string Name { get; }

    public async ValueTask EnqueueAsync(
        StreamId streamId,
        string payloadTypeName,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        long sequenceToken;
        lock (_lock)
        {
            sequenceToken = _nextSequenceToken++;
        }

        var messageId = $"{sequenceToken:D20}";
        var envelope = new FileQueueEnvelope(
            messageId,
            streamId.ProviderName,
            streamId.Namespace,
            streamId.Key,
            payloadTypeName,
            Convert.ToBase64String(payload),
            sequenceToken,
            DateTimeOffset.UtcNow);

        var filePath = Path.Combine(_directory, $"{messageId}.json");
        var json = JsonSerializer.Serialize(envelope);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<QueueMessage>> DequeueAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        var files = Directory.GetFiles(_directory, "*.json")
            .Where(f => !f.EndsWith(".ack.json", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Take(maxCount)
            .ToArray();

        var messages = new List<QueueMessage>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await File.ReadAllTextAsync(file, cancellationToken);
            var envelope = JsonSerializer.Deserialize<FileQueueEnvelope>(json);
            if (envelope is null) continue;

            messages.Add(new QueueMessage(
                envelope.MessageId,
                new StreamId(envelope.ProviderName, envelope.Namespace, envelope.Key),
                envelope.PayloadTypeName,
                Convert.FromBase64String(envelope.PayloadBase64),
                envelope.SequenceToken,
                envelope.EnqueuedUtc));
        }

        return messages;
    }

    public ValueTask AcknowledgeAsync(string messageId, CancellationToken cancellationToken = default)
    {
        var filePath = Path.Combine(_directory, $"{messageId}.json");
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        return ValueTask.CompletedTask;
    }

    private sealed record FileQueueEnvelope(
        string MessageId,
        string ProviderName,
        string Namespace,
        string Key,
        string PayloadTypeName,
        string PayloadBase64,
        long SequenceToken,
        DateTimeOffset EnqueuedUtc);
}
