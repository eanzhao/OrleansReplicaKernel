namespace OrleansReplicaKernel.Scheduling;

public sealed class GrainTypeSchedulingPolicy
{
    private readonly HashSet<string> _interleavableMethods;

    public GrainTypeSchedulingPolicy(IEnumerable<string>? interleavableMethods = null)
    {
        _interleavableMethods = interleavableMethods?
            .Where(methodName => !string.IsNullOrWhiteSpace(methodName))
            .ToHashSet(StringComparer.Ordinal)
            ?? [];
    }

    public static GrainTypeSchedulingPolicy Default { get; } = new();

    public IReadOnlyCollection<string> InterleavableMethods => _interleavableMethods;

    public bool AllowsInterleaving(string methodName)
        => _interleavableMethods.Contains(methodName);
}
