using System.Text;

namespace OrleansReplicaKernel.Serialization;

public ref struct BinaryBufferReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public BinaryBufferReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public bool IsCompleted => _position >= _buffer.Length;

    public void EnsureFullyConsumed()
    {
        if (!IsCompleted)
        {
            throw new InvalidOperationException("Binary payload contains unexpected trailing bytes.");
        }
    }

    public bool ReadBoolean()
    {
        var value = ReadByte();
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidOperationException($"Invalid boolean value '{value}'.")
        };
    }

    public byte ReadByte()
    {
        EnsureAvailable(1);
        return _buffer[_position++];
    }

    public DateTimeOffset ReadDateTimeOffset()
    {
        var ticks = ReadVarInt64();
        var offsetMinutes = ReadVarInt32();
        return new DateTimeOffset(ticks, TimeSpan.FromMinutes(offsetMinutes));
    }

    public float ReadFloat() => BitConverter.Int32BitsToSingle(ReadVarInt32());

    public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadVarInt64());

    public TimeSpan ReadTimeSpan() => new(ReadVarInt64());

    public Guid ReadGuid()
    {
        var span = ReadSpan(16);
        return new Guid(span);
    }

    public string ReadString()
    {
        var length = checked((int)ReadVarUInt32());
        if (length == 0)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(ReadSpan(length));
    }

    public BinaryBufferReader ReadSubReader(int length) => new(ReadSpan(length));

    public int ReadVarInt32() => BinaryZigZag.DecodeInt32(ReadVarUInt32());

    public long ReadVarInt64() => BinaryZigZag.DecodeInt64(ReadVarUInt64());

    public uint ReadVarUInt32()
    {
        uint result = 0;
        var shift = 0;

        while (shift < 35)
        {
            var current = ReadByte();
            result |= (uint)(current & 0x7f) << shift;
            if ((current & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }

        throw new InvalidOperationException("Invalid 32-bit varint payload.");
    }

    public ulong ReadVarUInt64()
    {
        ulong result = 0;
        var shift = 0;

        while (shift < 70)
        {
            var current = ReadByte();
            result |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }

        throw new InvalidOperationException("Invalid 64-bit varint payload.");
    }

    public ReadOnlySpan<byte> ReadSpan(int length)
    {
        EnsureAvailable(length);
        var span = _buffer.Slice(_position, length);
        _position += length;
        return span;
    }

    private void EnsureAvailable(int length)
    {
        if (_buffer.Length - _position < length)
        {
            throw new InvalidOperationException("Binary payload ended unexpectedly.");
        }
    }
}
