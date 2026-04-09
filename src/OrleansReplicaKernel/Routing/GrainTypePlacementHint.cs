namespace OrleansReplicaKernel.Routing;

public sealed record GrainTypePlacementHint(bool PreferLocalPlacement)
{
    public static GrainTypePlacementHint Default { get; } = new(PreferLocalPlacement: false);
}
