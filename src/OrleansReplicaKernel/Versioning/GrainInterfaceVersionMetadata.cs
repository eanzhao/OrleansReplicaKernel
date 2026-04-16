namespace OrleansReplicaKernel.Versioning;

public static class GrainInterfaceVersionMetadata
{
    public static GrainInterfaceVersionDescriptor FromContract(Type contractType, string grainType)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        ArgumentException.ThrowIfNullOrWhiteSpace(grainType);

        if (!contractType.IsInterface)
        {
            throw new InvalidOperationException(
                $"Grain interface version metadata can only be resolved from interface contracts. '{contractType.FullName}' is not an interface.");
        }

        var attribute = contractType.GetCustomAttributes(typeof(GrainInterfaceVersionAttribute), inherit: false)
            .OfType<GrainInterfaceVersionAttribute>()
            .SingleOrDefault();
        if (attribute is null)
        {
            return new GrainInterfaceVersionDescriptor(
                grainType,
                contractType.FullName ?? contractType.Name,
                1);
        }

        return new GrainInterfaceVersionDescriptor(
            grainType,
            attribute.CompatibilityFamily,
            attribute.Version);
    }
}
