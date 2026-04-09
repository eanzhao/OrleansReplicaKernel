namespace OrleansReplicaKernel.Routing;

public sealed record GrainTypeCollectionPolicy(TimeSpan? CollectionAgeLimit)
{
    public TimeSpan ResolveIdleWindow(TimeSpan defaultIdleWindow)
        => CollectionAgeLimit ?? defaultIdleWindow;
}
