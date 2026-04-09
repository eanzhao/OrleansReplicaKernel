namespace OrleansReplicaKernel.Invocation;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class GeneratedGrainImplementationAttribute : Attribute
{
    public GeneratedGrainImplementationAttribute(string grainType)
    {
        if (string.IsNullOrWhiteSpace(grainType))
        {
            throw new ArgumentException("Generated grain implementation grain type must be non-empty.", nameof(grainType));
        }

        GrainType = grainType;
    }

    public string GrainType { get; }

    public int CollectionAgeLimitMilliseconds { get; set; } = -1;
}
