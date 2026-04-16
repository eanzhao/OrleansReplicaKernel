namespace OrleansReplicaKernel.Scheduling;

public sealed class GrainTypeSchedulingPolicy
{
    private readonly HashSet<string> _interleavableMethods;

    public GrainTypeSchedulingPolicy(
        IEnumerable<string>? interleavableMethods = null,
        bool isReentrant = false,
        string? mayInterleavePredicateMethodName = null)
    {
        _interleavableMethods = interleavableMethods?
            .Where(methodName => !string.IsNullOrWhiteSpace(methodName))
            .ToHashSet(StringComparer.Ordinal)
            ?? [];
        IsReentrant = isReentrant;
        MayInterleavePredicateMethodName = mayInterleavePredicateMethodName;
    }

    public static GrainTypeSchedulingPolicy Default { get; } = new();

    public bool IsReentrant { get; }

    public string? MayInterleavePredicateMethodName { get; }

    public IReadOnlyCollection<string> InterleavableMethods => _interleavableMethods;

    public bool AllowsInterleaving(string methodName)
        => IsReentrant || _interleavableMethods.Contains(methodName);
}
