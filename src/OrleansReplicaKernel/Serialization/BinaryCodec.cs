namespace OrleansReplicaKernel.Serialization;

public abstract class BinaryCodec<T> : IBinaryCodec<T>
{
    public abstract string Alias { get; }

    public Type ValueType => typeof(T);

    public abstract T Read(ref BinaryBufferReader reader, BinarySerializer serializer);

    public abstract void Write(BinaryBufferWriter writer, T value, BinarySerializer serializer);

    object? IBinaryCodec.ReadUntyped(ref BinaryBufferReader reader, BinarySerializer serializer)
        => Read(ref reader, serializer);

    void IBinaryCodec.WriteUntyped(BinaryBufferWriter writer, object? value, BinarySerializer serializer)
    {
        if (value is not T typedValue)
        {
            throw new InvalidOperationException(
                $"Binary codec '{GetType().Name}' cannot write value of type '{value?.GetType().FullName ?? "<null>"}'.");
        }

        Write(writer, typedValue, serializer);
    }
}

public abstract class BinaryObjectCodec<T> : BinaryCodec<T>
{
    public sealed override T Read(ref BinaryBufferReader reader, BinarySerializer serializer)
    {
        var objectReader = new BinaryObjectReader(reader);
        var value = ReadFields(ref objectReader, serializer);
        objectReader.EnsureCompleted();
        reader = objectReader.Consume();
        return value;
    }

    public sealed override void Write(BinaryBufferWriter writer, T value, BinarySerializer serializer)
    {
        BinaryObjectWriter.WriteObject(writer, objectWriter => WriteFields(objectWriter, value, serializer));
    }

    protected abstract T ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer);

    protected abstract void WriteFields(BinaryObjectWriter writer, T value, BinarySerializer serializer);
}
