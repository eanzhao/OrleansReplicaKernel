using System.Buffers;
using System.Text;

namespace OrleansReplicaKernel.Serialization;

public sealed class BinaryBufferWriter
{
    [ThreadStatic] private static BinaryBufferWriter? _pooled;

    private readonly ArrayBufferWriter<byte> _buffer;

    public BinaryBufferWriter(int initialCapacity = 256)
    {
        _buffer = new ArrayBufferWriter<byte>(initialCapacity);
    }

    public int WrittenCount => _buffer.WrittenCount;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;

    public void WriteLengthPrefixed(Action<BinaryBufferWriter> writePayload)
    {
        var inner = RentPooled();
        try
        {
            writePayload(inner);
            WriteVarUInt32((uint)inner.WrittenCount);
            WriteBytes(inner.WrittenSpan);
        }
        finally
        {
            ReturnPooled(inner);
        }
    }

    private static BinaryBufferWriter RentPooled()
    {
        var pooled = _pooled;
        if (pooled is not null)
        {
            _pooled = null;
            pooled.Reset();
            return pooled;
        }

        return new BinaryBufferWriter();
    }

    private static void ReturnPooled(BinaryBufferWriter writer)
    {
        _pooled = writer;
    }

    public void Reset() => _buffer.ResetWrittenCount();

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteByte(byte value)
    {
        var span = _buffer.GetSpan(1);
        span[0] = value;
        _buffer.Advance(1);
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        bytes.CopyTo(_buffer.GetSpan(bytes.Length));
        _buffer.Advance(bytes.Length);
    }

    public void WriteDateTimeOffset(DateTimeOffset value)
    {
        WriteVarInt64(value.Ticks);
        WriteVarInt32((int)value.Offset.TotalMinutes);
    }

    public void WriteFloat(float value) => WriteVarInt32(BitConverter.SingleToInt32Bits(value));

    public void WriteDouble(double value) => WriteVarInt64(BitConverter.DoubleToInt64Bits(value));

    public void WriteTimeSpan(TimeSpan value) => WriteVarInt64(value.Ticks);

    public void WriteGuid(Guid value)
    {
        Span<byte> buffer = stackalloc byte[16];
        value.TryWriteBytes(buffer);
        WriteBytes(buffer);
    }

    public void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteVarUInt32((uint)byteCount);

        if (byteCount == 0)
        {
            return;
        }

        var span = _buffer.GetSpan(byteCount);
        var written = Encoding.UTF8.GetBytes(value, span);
        _buffer.Advance(written);
    }

    public void WriteVarInt32(int value) => WriteVarUInt32(BinaryZigZag.Encode(value));

    public void WriteVarInt64(long value) => WriteVarUInt64(BinaryZigZag.Encode(value));

    public void WriteVarUInt32(uint value)
    {
        while (value >= 0x80)
        {
            WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        WriteByte((byte)value);
    }

    public void WriteVarUInt64(ulong value)
    {
        while (value >= 0x80)
        {
            WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        WriteByte((byte)value);
    }
}

internal static class BinaryZigZag
{
    public static uint Encode(int value) => (uint)((value << 1) ^ (value >> 31));

    public static ulong Encode(long value) => (ulong)((value << 1) ^ (value >> 63));

    public static int DecodeInt32(uint value) => (int)((value >> 1) ^ (~(value & 1) + 1));

    public static long DecodeInt64(ulong value) => (long)((value >> 1) ^ (~(value & 1) + 1));
}
