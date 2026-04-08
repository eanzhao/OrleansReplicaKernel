namespace OrleansReplicaKernel.Invocation;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class GeneratedGrainReferenceAttribute : Attribute
{
    public GeneratedGrainReferenceAttribute(Type contractType, string grainType)
    {
        if (!contractType.IsInterface)
        {
            throw new ArgumentException(
                $"Generated grain reference contract '{contractType.Name}' must be an interface.",
                nameof(contractType));
        }

        if (string.IsNullOrWhiteSpace(grainType))
        {
            throw new ArgumentException("Generated grain reference grain type must be non-empty.", nameof(grainType));
        }

        ContractType = contractType;
        GrainType = grainType;
    }

    public Type ContractType { get; }

    public string GrainType { get; }
}
