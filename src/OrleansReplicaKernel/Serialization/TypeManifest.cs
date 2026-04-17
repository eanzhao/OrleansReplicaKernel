using System.Collections.Frozen;

namespace OrleansReplicaKernel.Serialization;

public sealed record TypeManifestEntry(string Alias, Type ConcreteType, Type[] BaseTypes);

public sealed class TypeManifest
{
    private readonly FrozenDictionary<string, TypeManifestEntry> _entriesByAlias;
    private readonly FrozenDictionary<Type, TypeManifestEntry> _entriesByType;
    private readonly FrozenDictionary<Type, TypeManifestEntry[]> _entriesByBaseType;

    private TypeManifest(IReadOnlyList<TypeManifestEntry> entries)
    {
        _entriesByAlias = entries.ToFrozenDictionary(e => e.Alias, StringComparer.Ordinal);
        _entriesByType = entries.ToFrozenDictionary(e => e.ConcreteType);

        _entriesByBaseType = entries
            .SelectMany(e => e.BaseTypes.Select(bt => (BaseType: bt, Entry: e)))
            .GroupBy(x => x.BaseType)
            .ToFrozenDictionary(g => g.Key, g => g.Select(x => x.Entry).ToArray());
    }

    public TypeManifestEntry? GetEntry(string alias)
        => _entriesByAlias.GetValueOrDefault(alias);

    public TypeManifestEntry? GetEntry(Type concreteType)
        => _entriesByType.GetValueOrDefault(concreteType);

    public IReadOnlyList<TypeManifestEntry> GetKnownSubtypes(Type baseType)
        => _entriesByBaseType.GetValueOrDefault(baseType) ?? [];

    public IEnumerable<TypeManifestEntry> Entries => _entriesByAlias.Values;

    public static TypeManifest Build(BinarySerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        var entries = new List<TypeManifestEntry>();
        foreach (var (type, codec) in serializer.RegisteredCodecs)
        {
            var baseTypes = type.GetInterfaces()
                .Concat(GetTypeHierarchy(type))
                .Where(t => t != typeof(object))
                .ToArray();

            entries.Add(new TypeManifestEntry(codec.Alias, type, baseTypes));
        }

        return new TypeManifest(entries);
    }

    private static IEnumerable<Type> GetTypeHierarchy(Type type)
    {
        var current = type.BaseType;
        while (current is not null && current != typeof(object))
        {
            yield return current;
            current = current.BaseType;
        }
    }
}
