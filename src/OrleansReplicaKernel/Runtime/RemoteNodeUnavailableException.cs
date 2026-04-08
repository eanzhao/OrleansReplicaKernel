namespace OrleansReplicaKernel.Runtime;

public sealed class RemoteNodeUnavailableException : Exception
{
    public RemoteNodeUnavailableException(string nodeName)
        : base($"Node '{nodeName}' is unavailable.")
    {
        NodeName = nodeName;
    }

    public string NodeName { get; }
}
