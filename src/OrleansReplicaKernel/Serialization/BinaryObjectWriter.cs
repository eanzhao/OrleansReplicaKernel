namespace OrleansReplicaKernel.Serialization;

public sealed class BinaryObjectWriter
{
    private readonly BinaryBufferWriter _writer;

    public BinaryObjectWriter(BinaryBufferWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public void Complete() => _writer.WriteVarUInt32(0);

    public void WriteDynamicField(int fieldId, object? value, BinarySerializer serializer)
        => WriteField(fieldId, payload => serializer.WriteDynamic(payload, value));

    public void WriteField<T>(int fieldId, T value, BinarySerializer serializer)
        => WriteField(fieldId, payload => serializer.Write(payload, value));

    public void WriteNullableField<T>(int fieldId, T? value, BinarySerializer serializer)
        where T : struct
        => WriteField(fieldId, payload => serializer.WriteNullable(payload, value));

    public void WriteOptionalField<T>(int fieldId, T? value, BinarySerializer serializer)
        where T : class
        => WriteField(fieldId, payload => serializer.WriteOptional(payload, value));

    public void WriteField(int fieldId, Action<BinaryBufferWriter> writePayload)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fieldId);
        ArgumentNullException.ThrowIfNull(writePayload);

        _writer.WriteVarUInt32((uint)fieldId);
        _writer.WriteLengthPrefixed(writePayload);
    }

    public static void WriteObject(BinaryBufferWriter writer, Action<BinaryObjectWriter> writeFields)
    {
        var objectWriter = new BinaryObjectWriter(writer);
        writeFields(objectWriter);
        objectWriter.Complete();
    }
}
