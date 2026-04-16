namespace OrleansReplicaKernel.Identity;

public readonly record struct SystemTargetId(string TargetType, string NodeName)
{
    private const string GrainTypePrefix = "$system/";

    public GrainId ToGrainId() => new($"{GrainTypePrefix}{TargetType}", NodeName);

    public static bool IsSystemTarget(GrainId grainId)
        => grainId.GrainType.StartsWith(GrainTypePrefix, StringComparison.Ordinal);

    public static SystemTargetId FromGrainId(GrainId grainId)
    {
        if (!IsSystemTarget(grainId))
        {
            throw new ArgumentException($"Grain id '{grainId}' is not a system target.", nameof(grainId));
        }

        var targetType = grainId.GrainType[GrainTypePrefix.Length..];
        if (string.IsNullOrWhiteSpace(targetType))
        {
            throw new ArgumentException($"System target grain id '{grainId}' does not include a target type.", nameof(grainId));
        }

        if (string.IsNullOrWhiteSpace(grainId.Key))
        {
            throw new ArgumentException($"System target grain id '{grainId}' does not include a node name.", nameof(grainId));
        }

        return new SystemTargetId(targetType, grainId.Key);
    }
}
