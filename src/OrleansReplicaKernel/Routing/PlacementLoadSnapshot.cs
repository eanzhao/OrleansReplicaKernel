namespace OrleansReplicaKernel.Routing;

public sealed record PlacementLoadSnapshot(
    IReadOnlyList<ActivationLoadRecord> Nodes)
{
    public int GetActivationCount(string nodeName)
        => Nodes.FirstOrDefault(item => string.Equals(item.NodeName, nodeName, StringComparison.Ordinal)).ActivationCount;
}
