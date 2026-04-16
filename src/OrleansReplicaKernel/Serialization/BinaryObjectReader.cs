namespace OrleansReplicaKernel.Serialization;

public ref struct BinaryObjectReader
{
    private BinaryBufferReader _reader;
    private bool _completed;

    public BinaryObjectReader(BinaryBufferReader reader)
    {
        _reader = reader;
        _completed = false;
    }

    public BinaryBufferReader Consume() => _reader;

    public void EnsureCompleted()
    {
        if (!_completed)
        {
            SkipRemainingFields();
        }

        _reader.EnsureFullyConsumed();
    }

    public void SkipRemainingFields()
    {
        while (TryReadField(out _, out _))
        {
        }
    }

    public bool TryReadField(out int fieldId, out BinaryBufferReader payload)
    {
        if (_completed)
        {
            fieldId = 0;
            payload = default;
            return false;
        }

        var encodedFieldId = _reader.ReadVarUInt32();
        if (encodedFieldId == 0)
        {
            _completed = true;
            fieldId = 0;
            payload = default;
            return false;
        }

        fieldId = checked((int)encodedFieldId);
        var payloadLength = checked((int)_reader.ReadVarUInt32());
        payload = _reader.ReadSubReader(payloadLength);
        return true;
    }
}
