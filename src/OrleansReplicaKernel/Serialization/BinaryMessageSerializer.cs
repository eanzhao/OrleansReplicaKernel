using OrleansReplicaKernel.Messaging;

namespace OrleansReplicaKernel.Serialization;

public sealed class BinaryMessageSerializer
{
    private readonly BinarySerializer _serializer;

    public BinaryMessageSerializer(BinarySerializer serializer)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    internal BinarySerializer Serializer => _serializer;

    public InvocationMessage DeserializeInvocationMessage(ReadOnlyMemory<byte> payload)
        => _serializer.Deserialize<InvocationMessage>(payload);

    public InvocationResponseMessage DeserializeInvocationResponse(ReadOnlyMemory<byte> payload)
        => _serializer.Deserialize<InvocationResponseMessage>(payload);

    public byte[] SerializeInvocationMessage(InvocationMessage message)
        => _serializer.Serialize(message);

    public byte[] SerializeInvocationResponse(InvocationResponseMessage response)
        => _serializer.Serialize(response);
}
