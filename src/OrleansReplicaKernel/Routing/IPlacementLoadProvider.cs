namespace OrleansReplicaKernel.Routing;

public interface IPlacementLoadProvider
{
    PlacementLoadSnapshot GetSnapshot();
}
