namespace OrleansReplicaKernel.Identity;

public readonly record struct GrainAddress(
    string NodeName,
    GrainId GrainId,
    long OwnerVersion)
{
    public override string ToString() => $"{NodeName}:{GrainId}@v{OwnerVersion}";
}
