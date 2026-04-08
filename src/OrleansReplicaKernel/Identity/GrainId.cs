namespace OrleansReplicaKernel.Identity;

public readonly record struct GrainId(string GrainType, string Key)
{
    public override string ToString() => $"{GrainType}/{Key}";
}
