namespace OrleansReplicaKernel.Routing;

public sealed record GrainTypePlacementHint(bool PreferLocalPlacement)
{
    public bool IsStatelessWorker { get; init; }

    public int MaxLocalWorkers { get; init; }

    public static GrainTypePlacementHint Default { get; } = new(PreferLocalPlacement: false);
}
