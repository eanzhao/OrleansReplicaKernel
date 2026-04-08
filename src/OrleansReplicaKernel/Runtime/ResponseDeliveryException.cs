namespace OrleansReplicaKernel.Runtime;

public sealed class ResponseDeliveryException : Exception
{
    public ResponseDeliveryException(string nodeName, string reason)
        : base($"Response delivery failed for node '{nodeName}': {reason}")
    {
        NodeName = nodeName;
        Reason = reason;
    }

    public string NodeName { get; }

    public string Reason { get; }
}
