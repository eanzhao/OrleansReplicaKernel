namespace OrleansReplicaKernel.Versioning;

[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class GrainInterfaceVersionAttribute : Attribute
{
    public GrainInterfaceVersionAttribute(string compatibilityFamily, int version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compatibilityFamily);
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Grain interface version must be positive.");
        }

        CompatibilityFamily = compatibilityFamily;
        Version = version;
    }

    public string CompatibilityFamily { get; }

    public int Version { get; }
}
