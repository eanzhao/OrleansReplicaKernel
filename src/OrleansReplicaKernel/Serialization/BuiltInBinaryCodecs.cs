using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Security;
using OrleansReplicaKernel.Transactions;

namespace OrleansReplicaKernel.Serialization;

internal sealed class BooleanBinaryCodec : BinaryCodec<bool>
{
    public override string Alias => "sys.bool";

    public override bool Read(ref BinaryBufferReader reader, BinarySerializer serializer) => reader.ReadBoolean();

    public override void Write(BinaryBufferWriter writer, bool value, BinarySerializer serializer) => writer.WriteBoolean(value);
}

internal sealed class ByteBinaryCodec : BinaryCodec<byte>
{
    public override string Alias => "sys.byte";

    public override byte Read(ref BinaryBufferReader reader, BinarySerializer serializer) => reader.ReadByte();

    public override void Write(BinaryBufferWriter writer, byte value, BinarySerializer serializer) => writer.WriteByte(value);
}

internal sealed class Int16BinaryCodec : BinaryCodec<short>
{
    public override string Alias => "sys.int16";

    public override short Read(ref BinaryBufferReader reader, BinarySerializer serializer) => checked((short)reader.ReadVarInt32());

    public override void Write(BinaryBufferWriter writer, short value, BinarySerializer serializer) => writer.WriteVarInt32(value);
}

internal sealed class UInt16BinaryCodec : BinaryCodec<ushort>
{
    public override string Alias => "sys.uint16";

    public override ushort Read(ref BinaryBufferReader reader, BinarySerializer serializer) => checked((ushort)reader.ReadVarUInt32());

    public override void Write(BinaryBufferWriter writer, ushort value, BinarySerializer serializer) => writer.WriteVarUInt32(value);
}

internal sealed class Int32BinaryCodec : BinaryCodec<int>
{
    public override string Alias => "sys.int32";

    public override int Read(ref BinaryBufferReader reader, BinarySerializer serializer) => reader.ReadVarInt32();

    public override void Write(BinaryBufferWriter writer, int value, BinarySerializer serializer) => writer.WriteVarInt32(value);
}

internal sealed class UInt32BinaryCodec : BinaryCodec<uint>
{
    public override string Alias => "sys.uint32";

    public override uint Read(ref BinaryBufferReader reader, BinarySerializer serializer) => reader.ReadVarUInt32();

    public override void Write(BinaryBufferWriter writer, uint value, BinarySerializer serializer) => writer.WriteVarUInt32(value);
}

internal sealed class Int64BinaryCodec : BinaryCodec<long>
{
    public override string Alias => "sys.int64";

    public override long Read(ref BinaryBufferReader reader, BinarySerializer serializer) => reader.ReadVarInt64();

    public override void Write(BinaryBufferWriter writer, long value, BinarySerializer serializer) => writer.WriteVarInt64(value);
}

internal sealed class FloatBinaryCodec : BinaryCodec<float>
{
    public override string Alias => "sys.float";

    public override float Read(ref BinaryBufferReader reader, BinarySerializer serializer) => reader.ReadFloat();

    public override void Write(BinaryBufferWriter writer, float value, BinarySerializer serializer) => writer.WriteFloat(value);
}

internal sealed class DoubleBinaryCodec : BinaryCodec<double>
{
    public override string Alias => "sys.double";

    public override double Read(ref BinaryBufferReader reader, BinarySerializer serializer) => reader.ReadDouble();

    public override void Write(BinaryBufferWriter writer, double value, BinarySerializer serializer) => writer.WriteDouble(value);
}

internal sealed class DecimalBinaryCodec : BinaryCodec<decimal>
{
    public override string Alias => "sys.decimal";

    public override decimal Read(ref BinaryBufferReader reader, BinarySerializer serializer)
    {
        var bits = new int[4];
        bits[0] = reader.ReadVarInt32();
        bits[1] = reader.ReadVarInt32();
        bits[2] = reader.ReadVarInt32();
        bits[3] = reader.ReadVarInt32();
        return new decimal(bits);
    }

    public override void Write(BinaryBufferWriter writer, decimal value, BinarySerializer serializer)
    {
        var bits = decimal.GetBits(value);
        writer.WriteVarInt32(bits[0]);
        writer.WriteVarInt32(bits[1]);
        writer.WriteVarInt32(bits[2]);
        writer.WriteVarInt32(bits[3]);
    }
}

internal sealed class StringBinaryCodec : BinaryCodec<string>
{
    public override string Alias => "sys.string";

    public override string Read(ref BinaryBufferReader reader, BinarySerializer serializer) => reader.ReadString();

    public override void Write(BinaryBufferWriter writer, string value, BinarySerializer serializer) => writer.WriteString(value);
}

internal sealed class GuidBinaryCodec : BinaryCodec<Guid>
{
    public override string Alias => "sys.guid";

    public override Guid Read(ref BinaryBufferReader reader, BinarySerializer serializer) => reader.ReadGuid();

    public override void Write(BinaryBufferWriter writer, Guid value, BinarySerializer serializer) => writer.WriteGuid(value);
}

internal sealed class DateTimeOffsetBinaryCodec : BinaryCodec<DateTimeOffset>
{
    public override string Alias => "sys.datetimeoffset";

    public override DateTimeOffset Read(ref BinaryBufferReader reader, BinarySerializer serializer) => reader.ReadDateTimeOffset();

    public override void Write(BinaryBufferWriter writer, DateTimeOffset value, BinarySerializer serializer)
        => writer.WriteDateTimeOffset(value);
}

internal sealed class TimeSpanBinaryCodec : BinaryCodec<TimeSpan>
{
    public override string Alias => "sys.timespan";

    public override TimeSpan Read(ref BinaryBufferReader reader, BinarySerializer serializer) => reader.ReadTimeSpan();

    public override void Write(BinaryBufferWriter writer, TimeSpan value, BinarySerializer serializer) => writer.WriteTimeSpan(value);
}

internal sealed class ByteArrayBinaryCodec : BinaryCodec<byte[]>
{
    public override string Alias => "sys.bytes";

    public override byte[] Read(ref BinaryBufferReader reader, BinarySerializer serializer)
    {
        var length = checked((int)reader.ReadVarUInt32());
        return reader.ReadSpan(length).ToArray();
    }

    public override void Write(BinaryBufferWriter writer, byte[] value, BinarySerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteVarUInt32((uint)value.Length);
        writer.WriteBytes(value);
    }
}

internal static class EnumBinaryCodec
{
    public const string Prefix = "sys.enum|";
    private const char AssemblyTypeSeparator = '|';

    public static IBinaryCodec Create(Type enumType)
    {
        ArgumentNullException.ThrowIfNull(enumType);

        if (!enumType.IsEnum)
        {
            throw new InvalidOperationException($"Type '{enumType.FullName}' is not an enum.");
        }

        var codecType = typeof(EnumBinaryCodec<>).MakeGenericType(enumType);
        return (IBinaryCodec)Activator.CreateInstance(codecType)!;
    }

    public static IBinaryCodec CreateFromAlias(string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        if (!alias.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Invalid enum codec alias '{alias}'.");
        }

        return Create(ResolveType(alias[Prefix.Length..]));
    }

    public static string GetAlias(Type enumType)
    {
        ArgumentNullException.ThrowIfNull(enumType);

        var assemblyName = enumType.Assembly.GetName().Name;
        var fullName = enumType.FullName;
        if (string.IsNullOrWhiteSpace(assemblyName) || string.IsNullOrWhiteSpace(fullName))
        {
            throw new InvalidOperationException($"Unable to resolve stable alias for enum '{enumType.FullName}'.");
        }

        return $"{Prefix}{assemblyName}{AssemblyTypeSeparator}{fullName}";
    }

    private static Type ResolveType(string typeDescriptor)
    {
        var separatorIndex = typeDescriptor.IndexOf(AssemblyTypeSeparator);
        if (separatorIndex <= 0 || separatorIndex == typeDescriptor.Length - 1)
        {
            throw new InvalidOperationException($"Invalid enum codec alias payload '{typeDescriptor}'.");
        }

        var assemblyName = typeDescriptor[..separatorIndex];
        var fullName = typeDescriptor[(separatorIndex + 1)..];
        var enumType = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal))
            .Select(assembly => assembly.GetType(fullName, throwOnError: false))
            .FirstOrDefault(type => type is not null)
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, throwOnError: false))
                .FirstOrDefault(type => type is not null)
            ?? throw new InvalidOperationException($"Unable to resolve enum type '{typeDescriptor}' for binary serialization.");

        if (!enumType.IsEnum)
        {
            throw new InvalidOperationException($"Type '{enumType.FullName}' is not an enum.");
        }

        return enumType;
    }
}

internal sealed class EnumBinaryCodec<TEnum> : BinaryCodec<TEnum>
    where TEnum : struct, Enum
{
    private static readonly TypeCode UnderlyingTypeCode = Type.GetTypeCode(Enum.GetUnderlyingType(typeof(TEnum)));

    public override string Alias => EnumBinaryCodec.GetAlias(typeof(TEnum));

    public override TEnum Read(ref BinaryBufferReader reader, BinarySerializer serializer)
        => UnderlyingTypeCode switch
        {
            TypeCode.Byte => (TEnum)Enum.ToObject(typeof(TEnum), reader.ReadByte()),
            TypeCode.SByte => (TEnum)Enum.ToObject(typeof(TEnum), checked((sbyte)reader.ReadVarInt32())),
            TypeCode.Int16 => (TEnum)Enum.ToObject(typeof(TEnum), checked((short)reader.ReadVarInt32())),
            TypeCode.UInt16 => (TEnum)Enum.ToObject(typeof(TEnum), checked((ushort)reader.ReadVarUInt32())),
            TypeCode.Int32 => (TEnum)Enum.ToObject(typeof(TEnum), reader.ReadVarInt32()),
            TypeCode.UInt32 => (TEnum)Enum.ToObject(typeof(TEnum), reader.ReadVarUInt32()),
            TypeCode.Int64 => (TEnum)Enum.ToObject(typeof(TEnum), reader.ReadVarInt64()),
            TypeCode.UInt64 => (TEnum)Enum.ToObject(typeof(TEnum), reader.ReadVarUInt64()),
            _ => throw new InvalidOperationException($"Enum '{typeof(TEnum).FullName}' uses unsupported underlying type '{Enum.GetUnderlyingType(typeof(TEnum)).FullName}'.")
        };

    public override void Write(BinaryBufferWriter writer, TEnum value, BinarySerializer serializer)
    {
        switch (UnderlyingTypeCode)
        {
            case TypeCode.Byte:
                writer.WriteByte(Convert.ToByte(value));
                return;
            case TypeCode.SByte:
                writer.WriteVarInt32(Convert.ToSByte(value));
                return;
            case TypeCode.Int16:
                writer.WriteVarInt32(Convert.ToInt16(value));
                return;
            case TypeCode.UInt16:
                writer.WriteVarUInt32(Convert.ToUInt16(value));
                return;
            case TypeCode.Int32:
                writer.WriteVarInt32(Convert.ToInt32(value));
                return;
            case TypeCode.UInt32:
                writer.WriteVarUInt32(Convert.ToUInt32(value));
                return;
            case TypeCode.Int64:
                writer.WriteVarInt64(Convert.ToInt64(value));
                return;
            case TypeCode.UInt64:
                writer.WriteVarUInt64(Convert.ToUInt64(value));
                return;
            default:
                throw new InvalidOperationException(
                    $"Enum '{typeof(TEnum).FullName}' uses unsupported underlying type '{Enum.GetUnderlyingType(typeof(TEnum)).FullName}'.");
        }
    }
}

internal sealed class ExceptionBinaryCodec : BinaryObjectCodec<Exception>
{
    public override string Alias => "sys.exception";

    protected override Exception ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        string? message = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            switch (fieldId)
            {
                case 1:
                    message = serializer.Read<string>(payload);
                    break;
            }
        }

        return new Exception(message ?? "Remote exception.");
    }

    protected override void WriteFields(BinaryObjectWriter writer, Exception value, BinarySerializer serializer)
    {
        if (!string.IsNullOrEmpty(value.Message))
        {
            writer.WriteField(1, value.Message, serializer);
        }
    }
}

internal sealed class InvalidOperationExceptionBinaryCodec : BinaryObjectCodec<InvalidOperationException>
{
    public override string Alias => "sys.invalid-operation";

    protected override InvalidOperationException ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        string? message = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            switch (fieldId)
            {
                case 1:
                    message = serializer.Read<string>(payload);
                    break;
            }
        }

        return new InvalidOperationException(message);
    }

    protected override void WriteFields(BinaryObjectWriter writer, InvalidOperationException value, BinarySerializer serializer)
    {
        if (!string.IsNullOrEmpty(value.Message))
        {
            writer.WriteField(1, value.Message, serializer);
        }
    }
}

internal sealed class ActivationQuiescingExceptionBinaryCodec : BinaryObjectCodec<ActivationQuiescingException>
{
    public override string Alias => "orleans.exception.activation-quiescing";

    protected override ActivationQuiescingException ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        GrainId? grainId = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            switch (fieldId)
            {
                case 1:
                    grainId = serializer.Read<GrainId>(payload);
                    break;
            }
        }

        return new ActivationQuiescingException(BinaryCodecRequired.Require(grainId, "GrainId"));
    }

    protected override void WriteFields(BinaryObjectWriter writer, ActivationQuiescingException value, BinarySerializer serializer)
    {
        writer.WriteField(1, value.GrainId, serializer);
    }
}

internal sealed class ActivationInitializationExceptionBinaryCodec : BinaryObjectCodec<ActivationInitializationException>
{
    public override string Alias => "orleans.exception.activation-initialization";

    protected override ActivationInitializationException ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        GrainId? grainId = null;
        string? message = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            switch (fieldId)
            {
                case 1:
                    grainId = serializer.Read<GrainId>(payload);
                    break;
                case 2:
                    message = serializer.Read<string>(payload);
                    break;
            }
        }

        return new ActivationInitializationException(
            BinaryCodecRequired.Require(grainId, nameof(ActivationInitializationException.GrainId)),
            message);
    }

    protected override void WriteFields(BinaryObjectWriter writer, ActivationInitializationException value, BinarySerializer serializer)
    {
        writer.WriteField(1, value.GrainId, serializer);
        if (!string.IsNullOrEmpty(value.Message))
        {
            writer.WriteField(2, value.Message, serializer);
        }
    }
}

internal sealed class StaleGrainAddressExceptionBinaryCodec : BinaryObjectCodec<StaleGrainAddressException>
{
    public override string Alias => "orleans.exception.stale-grain-address";

    protected override StaleGrainAddressException ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        GrainId? grainId = null;
        string? nodeName = null;
        long? messageOwnerVersion = null;
        long? fencedOwnerVersion = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            switch (fieldId)
            {
                case 1:
                    grainId = serializer.Read<GrainId>(payload);
                    break;
                case 2:
                    nodeName = serializer.Read<string>(payload);
                    break;
                case 3:
                    messageOwnerVersion = serializer.Read<long>(payload);
                    break;
                case 4:
                    fencedOwnerVersion = serializer.Read<long>(payload);
                    break;
            }
        }

        return new StaleGrainAddressException(
            BinaryCodecRequired.Require(grainId, nameof(StaleGrainAddressException.GrainId)),
            BinaryCodecRequired.Require(nodeName, nameof(StaleGrainAddressException.NodeName)),
            BinaryCodecRequired.Require(messageOwnerVersion, nameof(StaleGrainAddressException.MessageOwnerVersion)),
            BinaryCodecRequired.Require(fencedOwnerVersion, nameof(StaleGrainAddressException.FencedOwnerVersion)));
    }

    protected override void WriteFields(BinaryObjectWriter writer, StaleGrainAddressException value, BinarySerializer serializer)
    {
        writer.WriteField(1, value.GrainId, serializer);
        writer.WriteField(2, value.NodeName, serializer);
        writer.WriteField(3, value.MessageOwnerVersion, serializer);
        writer.WriteField(4, value.FencedOwnerVersion, serializer);
    }
}

internal sealed class RemoteNodeUnavailableExceptionBinaryCodec : BinaryObjectCodec<RemoteNodeUnavailableException>
{
    public override string Alias => "orleans.exception.remote-node-unavailable";

    protected override RemoteNodeUnavailableException ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        string? nodeName = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            switch (fieldId)
            {
                case 1:
                    nodeName = serializer.Read<string>(payload);
                    break;
            }
        }

        return new RemoteNodeUnavailableException(BinaryCodecRequired.Require(nodeName, nameof(RemoteNodeUnavailableException.NodeName)));
    }

    protected override void WriteFields(BinaryObjectWriter writer, RemoteNodeUnavailableException value, BinarySerializer serializer)
    {
        writer.WriteField(1, value.NodeName, serializer);
    }
}

internal sealed class ResponseDeliveryExceptionBinaryCodec : BinaryObjectCodec<ResponseDeliveryException>
{
    public override string Alias => "orleans.exception.response-delivery";

    protected override ResponseDeliveryException ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        string? nodeName = null;
        string? reason = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            switch (fieldId)
            {
                case 1:
                    nodeName = serializer.Read<string>(payload);
                    break;
                case 2:
                    reason = serializer.Read<string>(payload);
                    break;
            }
        }

        return new ResponseDeliveryException(
            BinaryCodecRequired.Require(nodeName, nameof(ResponseDeliveryException.NodeName)),
            BinaryCodecRequired.Require(reason, nameof(ResponseDeliveryException.Reason)));
    }

    protected override void WriteFields(BinaryObjectWriter writer, ResponseDeliveryException value, BinarySerializer serializer)
    {
        writer.WriteField(1, value.NodeName, serializer);
        writer.WriteField(2, value.Reason, serializer);
    }
}

internal sealed class GrainIdBinaryCodec : BinaryObjectCodec<GrainId>
{
    public override string Alias => "orleans.identity.grain-id";

    protected override GrainId ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        string? grainType = null;
        string? key = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            switch (fieldId)
            {
                case 1:
                    grainType = serializer.Read<string>(payload);
                    break;
                case 2:
                    key = serializer.Read<string>(payload);
                    break;
            }
        }

        return new GrainId(
            BinaryCodecRequired.Require(grainType, nameof(GrainId.GrainType)),
            BinaryCodecRequired.Require(key, nameof(GrainId.Key)));
    }

    protected override void WriteFields(BinaryObjectWriter writer, GrainId value, BinarySerializer serializer)
    {
        writer.WriteField(1, value.GrainType, serializer);
        writer.WriteField(2, value.Key, serializer);
    }
}

internal sealed class GrainAddressBinaryCodec : BinaryObjectCodec<GrainAddress>
{
    public override string Alias => "orleans.identity.grain-address";

    protected override GrainAddress ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        string? nodeName = null;
        GrainId? grainId = null;
        long? ownerVersion = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            switch (fieldId)
            {
                case 1:
                    nodeName = serializer.Read<string>(payload);
                    break;
                case 2:
                    grainId = serializer.Read<GrainId>(payload);
                    break;
                case 3:
                    ownerVersion = serializer.Read<long>(payload);
                    break;
            }
        }

        return new GrainAddress(
            BinaryCodecRequired.Require(nodeName, nameof(GrainAddress.NodeName)),
            BinaryCodecRequired.Require(grainId, nameof(GrainAddress.GrainId)),
            BinaryCodecRequired.Require(ownerVersion, nameof(GrainAddress.OwnerVersion)));
    }

    protected override void WriteFields(BinaryObjectWriter writer, GrainAddress value, BinarySerializer serializer)
    {
        writer.WriteField(1, value.NodeName, serializer);
        writer.WriteField(2, value.GrainId, serializer);
        writer.WriteField(3, value.OwnerVersion, serializer);
    }
}

internal sealed class ObjectReferenceDataBinaryCodec : BinaryObjectCodec<ObjectReferenceData>
{
    public override string Alias => "orleans.identity.object-reference";

    protected override ObjectReferenceData ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        string? interfaceName = null;
        GrainId? grainId = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            switch (fieldId)
            {
                case 1:
                    interfaceName = serializer.Read<string>(payload);
                    break;
                case 2:
                    grainId = serializer.Read<GrainId>(payload);
                    break;
            }
        }

        return new ObjectReferenceData(
            BinaryCodecRequired.Require(interfaceName, nameof(ObjectReferenceData.InterfaceName)),
            BinaryCodecRequired.Require(grainId, nameof(ObjectReferenceData.GrainId)));
    }

    protected override void WriteFields(BinaryObjectWriter writer, ObjectReferenceData value, BinarySerializer serializer)
    {
        writer.WriteField(1, value.InterfaceName, serializer);
        writer.WriteField(2, value.GrainId, serializer);
    }
}

internal sealed class InvocationIdentityBinaryCodec : BinaryObjectCodec<InvocationIdentity>
{
    public override string Alias => "orleans.security.invocation-identity";

    protected override InvocationIdentity ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        InvocationSourceKind? sourceKind = null;
        string? name = null;
        bool? isAuthenticated = null;
        string? certificateThumbprint = null;
        string? certificateSubject = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            switch (fieldId)
            {
                case 1:
                    sourceKind = (InvocationSourceKind)serializer.Read<int>(payload);
                    break;
                case 2:
                    name = serializer.Read<string>(payload);
                    break;
                case 3:
                    isAuthenticated = serializer.Read<bool>(payload);
                    break;
                case 4:
                    certificateThumbprint = serializer.ReadOptional<string>(payload);
                    break;
                case 5:
                    certificateSubject = serializer.ReadOptional<string>(payload);
                    break;
            }
        }

        return new InvocationIdentity(
            BinaryCodecRequired.Require(sourceKind, nameof(InvocationIdentity.SourceKind)),
            BinaryCodecRequired.Require(name, nameof(InvocationIdentity.Name)),
            BinaryCodecRequired.Require(isAuthenticated, nameof(InvocationIdentity.IsAuthenticated)),
            certificateThumbprint,
            certificateSubject);
    }

    protected override void WriteFields(BinaryObjectWriter writer, InvocationIdentity value, BinarySerializer serializer)
    {
        writer.WriteField(1, (int)value.SourceKind, serializer);
        writer.WriteField(2, value.Name, serializer);
        writer.WriteField(3, value.IsAuthenticated, serializer);
        writer.WriteOptionalField(4, value.CertificateThumbprint, serializer);
        writer.WriteOptionalField(5, value.CertificateSubject, serializer);
    }
}

internal sealed class InvocationMessageBinaryCodec : BinaryObjectCodec<InvocationMessage>
{
    public override string Alias => "orleans.message.invocation";

    protected override InvocationMessage ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        Guid? requestId = null;
        Guid? requestChainId = null;
        Guid? attemptId = null;
        int? attemptSequence = null;
        DateTimeOffset? createdUtc = null;
        string? sourceNodeName = null;
        GrainAddress? target = null;
        IInvokable? invokable = null;
        var sourceKind = InvocationSourceKind.ClusterNode;
        InvocationIdentity? identity = null;
        TransactionInfo? transaction = null;
        string? traceParent = null;
        string? traceState = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            switch (fieldId)
            {
                case 1:
                    requestId = serializer.Read<Guid>(payload);
                    break;
                case 2:
                    requestChainId = serializer.Read<Guid>(payload);
                    break;
                case 3:
                    attemptId = serializer.Read<Guid>(payload);
                    break;
                case 4:
                    attemptSequence = serializer.Read<int>(payload);
                    break;
                case 5:
                    createdUtc = serializer.Read<DateTimeOffset>(payload);
                    break;
                case 6:
                    sourceNodeName = serializer.Read<string>(payload);
                    break;
                case 7:
                    target = serializer.Read<GrainAddress>(payload);
                    break;
                case 8:
                    invokable = serializer.ReadDynamic(payload) as IInvokable;
                    break;
                case 9:
                    sourceKind = (InvocationSourceKind)serializer.Read<int>(payload);
                    break;
                case 10:
                    identity = serializer.ReadOptional<InvocationIdentity>(payload);
                    break;
                case 11:
                    transaction = serializer.ReadOptional<TransactionInfo>(payload);
                    break;
                case 12:
                    traceParent = serializer.ReadOptional<string>(payload);
                    break;
                case 13:
                    traceState = serializer.ReadOptional<string>(payload);
                    break;
            }
        }

        return new InvocationMessage(
            BinaryCodecRequired.Require(requestId, nameof(InvocationMessage.RequestId)),
            BinaryCodecRequired.Require(requestChainId, nameof(InvocationMessage.RequestChainId)),
            BinaryCodecRequired.Require(attemptId, nameof(InvocationMessage.AttemptId)),
            BinaryCodecRequired.Require(attemptSequence, nameof(InvocationMessage.AttemptSequence)),
            BinaryCodecRequired.Require(createdUtc, nameof(InvocationMessage.CreatedUtc)),
            BinaryCodecRequired.Require(sourceNodeName, nameof(InvocationMessage.SourceNodeName)),
            BinaryCodecRequired.Require(target, nameof(InvocationMessage.Target)),
            invokable ?? throw new InvalidOperationException("InvocationMessage is missing invokable payload."),
            sourceKind,
            identity,
            transaction,
            traceParent,
            traceState);
    }

    protected override void WriteFields(BinaryObjectWriter writer, InvocationMessage value, BinarySerializer serializer)
    {
        writer.WriteField(1, value.RequestId, serializer);
        writer.WriteField(2, value.RequestChainId, serializer);
        writer.WriteField(3, value.AttemptId, serializer);
        writer.WriteField(4, value.AttemptSequence, serializer);
        writer.WriteField(5, value.CreatedUtc, serializer);
        writer.WriteField(6, value.SourceNodeName, serializer);
        writer.WriteField(7, value.Target, serializer);
        writer.WriteDynamicField(8, value.Invokable, serializer);
        writer.WriteField(9, (int)value.SourceKind, serializer);
        writer.WriteOptionalField(10, value.Identity, serializer);
        writer.WriteOptionalField(11, value.Transaction, serializer);
        writer.WriteOptionalField(12, value.TraceParent, serializer);
        writer.WriteOptionalField(13, value.TraceState, serializer);
    }
}

internal sealed class InvocationResponseMessageBinaryCodec : BinaryObjectCodec<InvocationResponseMessage>
{
    public override string Alias => "orleans.message.invocation-response";

    protected override InvocationResponseMessage ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
    {
        Guid? requestId = null;
        Guid? attemptId = null;
        int? attemptSequence = null;
        string? responderNodeName = null;
        object? result = null;
        Exception? error = null;

        while (reader.TryReadField(out var fieldId, out var payload))
        {
            switch (fieldId)
            {
                case 1:
                    requestId = serializer.Read<Guid>(payload);
                    break;
                case 2:
                    attemptId = serializer.Read<Guid>(payload);
                    break;
                case 3:
                    attemptSequence = serializer.Read<int>(payload);
                    break;
                case 4:
                    responderNodeName = serializer.Read<string>(payload);
                    break;
                case 5:
                    result = serializer.ReadDynamic(payload);
                    break;
                case 6:
                    error = serializer.ReadDynamic(payload) as Exception;
                    break;
            }
        }

        return new InvocationResponseMessage(
            BinaryCodecRequired.Require(requestId, nameof(InvocationResponseMessage.RequestId)),
            BinaryCodecRequired.Require(attemptId, nameof(InvocationResponseMessage.AttemptId)),
            BinaryCodecRequired.Require(attemptSequence, nameof(InvocationResponseMessage.AttemptSequence)),
            BinaryCodecRequired.Require(responderNodeName, nameof(InvocationResponseMessage.ResponderNodeName)),
            result,
            error);
    }

    protected override void WriteFields(BinaryObjectWriter writer, InvocationResponseMessage value, BinarySerializer serializer)
    {
        writer.WriteField(1, value.RequestId, serializer);
        writer.WriteField(2, value.AttemptId, serializer);
        writer.WriteField(3, value.AttemptSequence, serializer);
        writer.WriteField(4, value.ResponderNodeName, serializer);
        writer.WriteDynamicField(5, value.Result, serializer);
        writer.WriteDynamicField(6, value.Error, serializer);
    }
}

internal static class BinaryCodecRequired
{
    public static T Require<T>(T? value, string name) where T : struct
        => value ?? throw new InvalidOperationException($"Binary payload is missing required field '{name}'.");

    public static T Require<T>(T? value, string name) where T : class
        => value ?? throw new InvalidOperationException($"Binary payload is missing required field '{name}'.");
}
