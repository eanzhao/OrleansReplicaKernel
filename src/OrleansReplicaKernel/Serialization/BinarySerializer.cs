namespace OrleansReplicaKernel.Serialization;

public sealed class BinarySerializer
{
    private readonly Lock _codecLock = new();
    private Dictionary<string, IBinaryCodec> _codecsByAlias;
    private Dictionary<Type, IBinaryCodec> _codecsByType;

    public BinarySerializer(IEnumerable<IBinaryCodec> codecs)
    {
        ArgumentNullException.ThrowIfNull(codecs);

        _codecsByType = new Dictionary<Type, IBinaryCodec>();
        _codecsByAlias = new Dictionary<string, IBinaryCodec>(StringComparer.Ordinal);

        foreach (var codec in codecs)
        {
            RegisterLocked(codec);
        }
    }

    public T Deserialize<T>(ReadOnlyMemory<byte> payload)
    {
        var reader = new BinaryBufferReader(payload.Span);
        var value = Read<T>(ref reader);
        return value;
    }

    public IBinaryCodec<T> GetCodec<T>()
    {
        var codec = ResolveCodec(typeof(T));
        if (codec is not IBinaryCodec<T> typedCodec)
        {
            throw new InvalidOperationException(
                $"Binary codec '{codec.GetType().Name}' does not support '{typeof(T).FullName}'.");
        }

        return typedCodec;
    }

    public T Read<T>(BinaryBufferReader reader)
    {
        var localReader = reader;
        return Read<T>(ref localReader);
    }

    public T Read<T>(ref BinaryBufferReader reader)
    {
        var codec = GetCodec<T>();
        var value = codec.Read(ref reader, this);
        reader.EnsureFullyConsumed();
        return value;
    }

    public object? ReadDynamic(BinaryBufferReader reader)
    {
        var localReader = reader;
        return ReadDynamic(ref localReader);
    }

    public object? ReadDynamic(ref BinaryBufferReader reader)
    {
        if (!reader.ReadBoolean())
        {
            return null;
        }

        var alias = reader.ReadString();
        var payloadLength = checked((int)reader.ReadVarUInt32());
        var payload = reader.ReadSubReader(payloadLength);

        var codec = ResolveCodec(alias);
        var value = codec.ReadUntyped(ref payload, this);
        payload.EnsureFullyConsumed();
        return value;
    }

    public T? ReadNullable<T>(BinaryBufferReader reader)
        where T : struct
    {
        var localReader = reader;
        return ReadNullable<T>(ref localReader);
    }

    public T? ReadNullable<T>(ref BinaryBufferReader reader)
        where T : struct
    {
        if (!reader.ReadBoolean())
        {
            return null;
        }

        var payloadLength = checked((int)reader.ReadVarUInt32());
        var payload = reader.ReadSubReader(payloadLength);
        var value = GetCodec<T>().Read(ref payload, this);
        payload.EnsureFullyConsumed();
        return value;
    }

    public T? ReadOptional<T>(BinaryBufferReader reader)
        where T : class
    {
        var localReader = reader;
        return ReadOptional<T>(ref localReader);
    }

    public T? ReadOptional<T>(ref BinaryBufferReader reader)
        where T : class
    {
        if (!reader.ReadBoolean())
        {
            return null;
        }

        var payloadLength = checked((int)reader.ReadVarUInt32());
        var payload = reader.ReadSubReader(payloadLength);
        var value = GetCodec<T>().Read(ref payload, this);
        payload.EnsureFullyConsumed();
        return value;
    }

    public byte[] Serialize<T>(T value)
    {
        var writer = new BinaryBufferWriter();
        Write(writer, value);
        return writer.ToArray();
    }

    public void Write<T>(BinaryBufferWriter writer, T value)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            throw new InvalidOperationException("Typed binary serialization does not support null values. Use dynamic serialization for nullable payloads.");
        }

        var codec = ResolveCodec(value.GetType());
        codec.WriteUntyped(writer, value, this);
    }

    public void WriteDynamic(BinaryBufferWriter writer, object? value)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteBoolean(value is not null);
        if (value is null)
        {
            return;
        }

        var codec = ResolveCodec(value.GetType());
        writer.WriteString(codec.Alias);
        writer.WriteLengthPrefixed(payload => codec.WriteUntyped(payload, value, this));
    }

    public void WriteNullable<T>(BinaryBufferWriter writer, T? value)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteBoolean(value.HasValue);
        if (!value.HasValue)
        {
            return;
        }

        writer.WriteLengthPrefixed(payload => GetCodec<T>().Write(payload, value.Value, this));
    }

    public void WriteOptional<T>(BinaryBufferWriter writer, T? value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteBoolean(value is not null);
        if (value is null)
        {
            return;
        }

        writer.WriteLengthPrefixed(payload => GetCodec<T>().Write(payload, value, this));
    }

    private void RegisterLocked(IBinaryCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);

        if (_codecsByType.TryGetValue(codec.ValueType, out var existingByType))
        {
            if (existingByType.GetType() == codec.GetType())
            {
                return;
            }

            throw new InvalidOperationException(
                $"Binary codec already registered for type '{codec.ValueType.FullName}': '{existingByType.GetType().Name}'.");
        }

        if (_codecsByAlias.TryGetValue(codec.Alias, out var existingByAlias))
        {
            if (existingByAlias.GetType() == codec.GetType())
            {
                return;
            }

            throw new InvalidOperationException(
                $"Binary codec alias '{codec.Alias}' is already registered by '{existingByAlias.GetType().Name}'.");
        }

        var newByType = new Dictionary<Type, IBinaryCodec>(_codecsByType) { [codec.ValueType] = codec };
        var newByAlias = new Dictionary<string, IBinaryCodec>(_codecsByAlias, StringComparer.Ordinal) { [codec.Alias] = codec };
        _codecsByType = newByType;
        _codecsByAlias = newByAlias;
    }

    private IBinaryCodec ResolveCodec(string alias)
    {
        if (_codecsByAlias.TryGetValue(alias, out var codec))
        {
            return codec;
        }

        lock (_codecLock)
        {
            if (_codecsByAlias.TryGetValue(alias, out codec))
            {
                return codec;
            }

            codec = CreateCodecFromAlias(alias)
                ?? throw new InvalidOperationException($"No binary codec registered for alias '{alias}'.");
            RegisterLocked(codec);
            return codec;
        }
    }

    private IBinaryCodec ResolveCodec(Type type)
    {
        if (_codecsByType.TryGetValue(type, out var codec))
        {
            return codec;
        }

        lock (_codecLock)
        {
            if (_codecsByType.TryGetValue(type, out codec))
            {
                return codec;
            }

            codec = CreateCodecFromType(type)
                ?? throw new InvalidOperationException($"No binary codec registered for type '{type.FullName}'.");
            RegisterLocked(codec);
            return codec;
        }
    }

    private IBinaryCodec? CreateCodecFromAlias(string alias)
    {
        if (alias.StartsWith(ArrayBinaryCodec.Prefix, StringComparison.Ordinal))
        {
            var elementAlias = alias[ArrayBinaryCodec.Prefix.Length..];
            var elementCodec = ResolveCodec(elementAlias);
            return ArrayBinaryCodec.Create(elementCodec);
        }

        if (alias.StartsWith(ListBinaryCodec.Prefix, StringComparison.Ordinal))
        {
            var elementAlias = alias[ListBinaryCodec.Prefix.Length..];
            var elementCodec = ResolveCodec(elementAlias);
            return ListBinaryCodec.Create(elementCodec);
        }

        if (alias.StartsWith(DictionaryBinaryCodec.Prefix, StringComparison.Ordinal))
        {
            var pairAliases = alias[DictionaryBinaryCodec.Prefix.Length..];
            var separatorIndex = pairAliases.IndexOf('|');
            if (separatorIndex <= 0 || separatorIndex == pairAliases.Length - 1)
            {
                throw new InvalidOperationException($"Invalid dictionary codec alias '{alias}'.");
            }

            var keyCodec = ResolveCodec(pairAliases[..separatorIndex]);
            var valueCodec = ResolveCodec(pairAliases[(separatorIndex + 1)..]);
            return DictionaryBinaryCodec.Create(keyCodec, valueCodec);
        }

        if (alias.StartsWith(HashSetBinaryCodec.Prefix, StringComparison.Ordinal))
        {
            var elementAlias = alias[HashSetBinaryCodec.Prefix.Length..];
            var elementCodec = ResolveCodec(elementAlias);
            return HashSetBinaryCodec.Create(elementCodec);
        }

        return null;
    }

    private IBinaryCodec? CreateCodecFromType(Type type)
    {
        if (type.IsArray && type.GetArrayRank() == 1)
        {
            var elementCodec = ResolveCodec(type.GetElementType()!);
            return ArrayBinaryCodec.Create(elementCodec);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var elementCodec = ResolveCodec(type.GetGenericArguments()[0]);
            return ListBinaryCodec.Create(elementCodec);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var arguments = type.GetGenericArguments();
            var keyCodec = ResolveCodec(arguments[0]);
            var valueCodec = ResolveCodec(arguments[1]);
            return DictionaryBinaryCodec.Create(keyCodec, valueCodec);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(HashSet<>))
        {
            var elementCodec = ResolveCodec(type.GetGenericArguments()[0]);
            return HashSetBinaryCodec.Create(elementCodec);
        }

        if (typeof(InvalidOperationException).IsAssignableFrom(type)
            && _codecsByType.TryGetValue(typeof(InvalidOperationException), out var invalidOperationCodec))
        {
            return invalidOperationCodec;
        }

        if (typeof(Exception).IsAssignableFrom(type)
            && _codecsByType.TryGetValue(typeof(Exception), out var exceptionCodec))
        {
            return exceptionCodec;
        }

        return null;
    }
}

internal static class ArrayBinaryCodec
{
    public const string Prefix = "sys.array|";

    public static IBinaryCodec Create(IBinaryCodec elementCodec)
    {
        var codecType = typeof(ArrayBinaryCodec<>).MakeGenericType(elementCodec.ValueType);
        return (IBinaryCodec)Activator.CreateInstance(codecType, elementCodec)!;
    }
}

internal sealed class ArrayBinaryCodec<T> : BinaryCodec<T[]>
{
    private readonly IBinaryCodec<T> _elementCodec;

    public ArrayBinaryCodec(IBinaryCodec<T> elementCodec)
    {
        _elementCodec = elementCodec ?? throw new ArgumentNullException(nameof(elementCodec));
    }

    public override string Alias => $"{ArrayBinaryCodec.Prefix}{_elementCodec.Alias}";

    public override T[] Read(ref BinaryBufferReader reader, BinarySerializer serializer)
    {
        var count = checked((int)reader.ReadVarUInt32());
        var items = new T[count];

        for (var i = 0; i < count; i++)
        {
            var itemLength = checked((int)reader.ReadVarUInt32());
            var payload = reader.ReadSubReader(itemLength);
            items[i] = _elementCodec.Read(ref payload, serializer);
            payload.EnsureFullyConsumed();
        }

        return items;
    }

    public override void Write(BinaryBufferWriter writer, T[] value, BinarySerializer serializer)
    {
        writer.WriteVarUInt32((uint)value.Length);

        foreach (var item in value)
        {
            writer.WriteLengthPrefixed(payload => _elementCodec.Write(payload, item, serializer));
        }
    }
}

internal static class ListBinaryCodec
{
    public const string Prefix = "sys.list|";

    public static IBinaryCodec Create(IBinaryCodec elementCodec)
    {
        var codecType = typeof(ListBinaryCodec<>).MakeGenericType(elementCodec.ValueType);
        return (IBinaryCodec)Activator.CreateInstance(codecType, elementCodec)!;
    }
}

internal sealed class ListBinaryCodec<T> : BinaryCodec<List<T>>
{
    private readonly IBinaryCodec<T> _elementCodec;

    public ListBinaryCodec(IBinaryCodec<T> elementCodec)
    {
        _elementCodec = elementCodec ?? throw new ArgumentNullException(nameof(elementCodec));
    }

    public override string Alias => $"{ListBinaryCodec.Prefix}{_elementCodec.Alias}";

    public override List<T> Read(ref BinaryBufferReader reader, BinarySerializer serializer)
    {
        var count = checked((int)reader.ReadVarUInt32());
        var items = new List<T>(count);

        for (var i = 0; i < count; i++)
        {
            var itemLength = checked((int)reader.ReadVarUInt32());
            var payload = reader.ReadSubReader(itemLength);
            items.Add(_elementCodec.Read(ref payload, serializer));
            payload.EnsureFullyConsumed();
        }

        return items;
    }

    public override void Write(BinaryBufferWriter writer, List<T> value, BinarySerializer serializer)
    {
        writer.WriteVarUInt32((uint)value.Count);

        foreach (var item in value)
        {
            writer.WriteLengthPrefixed(payload => _elementCodec.Write(payload, item, serializer));
        }
    }
}

internal static class DictionaryBinaryCodec
{
    public const string Prefix = "sys.dictionary|";

    public static IBinaryCodec Create(IBinaryCodec keyCodec, IBinaryCodec valueCodec)
    {
        var codecType = typeof(DictionaryBinaryCodec<,>).MakeGenericType(keyCodec.ValueType, valueCodec.ValueType);
        return (IBinaryCodec)Activator.CreateInstance(codecType, keyCodec, valueCodec)!;
    }
}

internal sealed class DictionaryBinaryCodec<TKey, TValue> : BinaryCodec<Dictionary<TKey, TValue>>
    where TKey : notnull
{
    private readonly IBinaryCodec<TKey> _keyCodec;
    private readonly IBinaryCodec<TValue> _valueCodec;

    public DictionaryBinaryCodec(IBinaryCodec<TKey> keyCodec, IBinaryCodec<TValue> valueCodec)
    {
        _keyCodec = keyCodec ?? throw new ArgumentNullException(nameof(keyCodec));
        _valueCodec = valueCodec ?? throw new ArgumentNullException(nameof(valueCodec));
    }

    public override string Alias => $"{DictionaryBinaryCodec.Prefix}{_keyCodec.Alias}|{_valueCodec.Alias}";

    public override Dictionary<TKey, TValue> Read(ref BinaryBufferReader reader, BinarySerializer serializer)
    {
        var count = checked((int)reader.ReadVarUInt32());
        var items = new Dictionary<TKey, TValue>(count);

        for (var i = 0; i < count; i++)
        {
            var keyLength = checked((int)reader.ReadVarUInt32());
            var keyPayload = reader.ReadSubReader(keyLength);
            var key = _keyCodec.Read(ref keyPayload, serializer);
            keyPayload.EnsureFullyConsumed();

            var valueLength = checked((int)reader.ReadVarUInt32());
            var valuePayload = reader.ReadSubReader(valueLength);
            var value = _valueCodec.Read(ref valuePayload, serializer);
            valuePayload.EnsureFullyConsumed();

            items.Add(key, value);
        }

        return items;
    }

    public override void Write(BinaryBufferWriter writer, Dictionary<TKey, TValue> value, BinarySerializer serializer)
    {
        writer.WriteVarUInt32((uint)value.Count);

        foreach (var pair in value)
        {
            writer.WriteLengthPrefixed(payload => _keyCodec.Write(payload, pair.Key, serializer));
            writer.WriteLengthPrefixed(payload => _valueCodec.Write(payload, pair.Value, serializer));
        }
    }
}

internal static class HashSetBinaryCodec
{
    public const string Prefix = "sys.hash-set|";

    public static IBinaryCodec Create(IBinaryCodec elementCodec)
    {
        var codecType = typeof(HashSetBinaryCodec<>).MakeGenericType(elementCodec.ValueType);
        return (IBinaryCodec)Activator.CreateInstance(codecType, elementCodec)!;
    }
}

internal sealed class HashSetBinaryCodec<T> : BinaryCodec<HashSet<T>>
    where T : notnull
{
    private readonly IBinaryCodec<T> _elementCodec;

    public HashSetBinaryCodec(IBinaryCodec<T> elementCodec)
    {
        _elementCodec = elementCodec ?? throw new ArgumentNullException(nameof(elementCodec));
    }

    public override string Alias => $"{HashSetBinaryCodec.Prefix}{_elementCodec.Alias}";

    public override HashSet<T> Read(ref BinaryBufferReader reader, BinarySerializer serializer)
    {
        var count = checked((int)reader.ReadVarUInt32());
        var items = new HashSet<T>();

        for (var i = 0; i < count; i++)
        {
            var itemLength = checked((int)reader.ReadVarUInt32());
            var payload = reader.ReadSubReader(itemLength);
            items.Add(_elementCodec.Read(ref payload, serializer));
            payload.EnsureFullyConsumed();
        }

        return items;
    }

    public override void Write(BinaryBufferWriter writer, HashSet<T> value, BinarySerializer serializer)
    {
        writer.WriteVarUInt32((uint)value.Count);

        foreach (var item in value)
        {
            writer.WriteLengthPrefixed(payload => _elementCodec.Write(payload, item, serializer));
        }
    }
}
