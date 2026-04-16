using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;

namespace OrleansReplicaKernel.Versioning;

public sealed record GrainInterfaceVersionDescriptor
{
    public GrainInterfaceVersionDescriptor(string grainType, string compatibilityFamily, int version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grainType);
        ArgumentException.ThrowIfNullOrWhiteSpace(compatibilityFamily);
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Grain interface version must be positive.");
        }

        GrainType = grainType;
        CompatibilityFamily = compatibilityFamily;
        Version = version;
    }

    public string GrainType { get; }

    public string CompatibilityFamily { get; }

    public int Version { get; }

    public static GrainInterfaceVersionDescriptor FromInvocation(GrainId grainId, IInvokable invokable)
    {
        ArgumentNullException.ThrowIfNull(invokable);
        return new GrainInterfaceVersionDescriptor(
            grainId.GrainType,
            invokable.InterfaceCompatibilityFamily,
            invokable.InterfaceVersion);
    }
}

public sealed record NodeGrainInterfaceVersionManifest
{
    public NodeGrainInterfaceVersionManifest(string nodeName, GrainInterfaceVersionDescriptor[] supportedInterfaces)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);

        NodeName = nodeName;
        SupportedInterfaces = supportedInterfaces ?? [];
    }

    public string NodeName { get; }

    public GrainInterfaceVersionDescriptor[] SupportedInterfaces { get; }
}

public sealed record GrainInterfaceVersionManifestCheckpoint
{
    public static readonly GrainInterfaceVersionManifestCheckpoint Empty = new([]);

    public GrainInterfaceVersionManifestCheckpoint(NodeGrainInterfaceVersionManifest[] nodes)
    {
        Nodes = nodes ?? [];
    }

    public NodeGrainInterfaceVersionManifest[] Nodes { get; }
}
