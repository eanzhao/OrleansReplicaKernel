namespace OrleansReplicaKernel.Serialization;

public interface IBinaryCodec
{
    string Alias { get; }

    Type ValueType { get; }

    object? ReadUntyped(ref BinaryBufferReader reader, BinarySerializer serializer);

    void WriteUntyped(BinaryBufferWriter writer, object? value, BinarySerializer serializer);
}

public interface IBinaryCodec<T> : IBinaryCodec
{
    T Read(ref BinaryBufferReader reader, BinarySerializer serializer);

    void Write(BinaryBufferWriter writer, T value, BinarySerializer serializer);
}
