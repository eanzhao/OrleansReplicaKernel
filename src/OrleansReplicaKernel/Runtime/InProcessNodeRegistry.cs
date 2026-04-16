namespace OrleansReplicaKernel.Runtime;

public sealed class InProcessNodeRegistry : IProbeReachabilityController, ITransportFaultInjector
{
    private readonly object _lock = new();
    private readonly Dictionary<string, NodeState> _nodes = new();

    public void Register(
        string nodeName,
        IMessageReceiver requestReceiver,
        IResponseReceiver responseReceiver)
    {
        lock (_lock)
        {
            _nodes.Add(nodeName, new NodeState(requestReceiver, responseReceiver));
        }
    }

    public void DelayNextRequest(string nodeName, TimeSpan delay) =>
        MutateNode(nodeName, node => node.NextDelay = delay);

    public void FailNextRequestWithResponse(string nodeName, Exception error) =>
        MutateNode(nodeName, node => node.NextResponseError = error);

    public void DropNextResponse(string nodeName, string reason) =>
        MutateNode(nodeName, node =>
        {
            node.NextDroppedResponseReason = reason;
            node.NextDroppedResponseReplayDelay = null;
        });

    public void DropNextResponseAndReplayLater(string nodeName, TimeSpan replayDelay, string reason) =>
        MutateNode(nodeName, node =>
        {
            node.NextDroppedResponseReason = reason;
            node.NextDroppedResponseReplayDelay = replayDelay;
        });

    public void DuplicateNextResponse(string nodeName, TimeSpan duplicateDelay) =>
        MutateNode(nodeName, node => node.NextDuplicateResponseDelay = duplicateDelay);

    public DispatchPlan PrepareDispatch(string sourceNodeName, string targetNodeName)
    {
        lock (_lock)
        {
            var targetNode = GetNodeLocked(targetNodeName);
            var sourceNode = GetNodeLocked(sourceNodeName);

            var plan = new DispatchPlan(
                targetNode.RequestReceiver,
                sourceNode.ResponseReceiver,
                targetNode.NextDelay,
                targetNode.NextResponseError,
                targetNode.NextDroppedResponseReason,
                targetNode.NextDroppedResponseReplayDelay,
                targetNode.NextDuplicateResponseDelay);

            targetNode.NextDelay = null;
            targetNode.NextResponseError = null;
            targetNode.NextDroppedResponseReason = null;
            targetNode.NextDroppedResponseReplayDelay = null;
            targetNode.NextDuplicateResponseDelay = null;

            return plan;
        }
    }

    public void SetProbeReachable(string nodeName, bool isReachable) =>
        MutateNode(nodeName, node => node.IsProbeReachable = isReachable);

    public void FailNextProbe(string nodeName, string failureReason) =>
        MutateNode(nodeName, node => node.NextProbeFailureReason = failureReason);

    public bool Probe(string sourceNodeName, string targetNodeName, out string reason)
    {
        lock (_lock)
        {
            if (!_nodes.TryGetValue(targetNodeName, out var node))
            {
                reason = $"probe target '{targetNodeName}' is not registered";
                return false;
            }

            if (!node.IsProbeReachable)
            {
                reason = $"probe {sourceNodeName} -> {targetNodeName} missed";
                return false;
            }

            if (node.NextProbeFailureReason is { } injectedReason)
            {
                node.NextProbeFailureReason = null;
                reason = $"probe {sourceNodeName} -> {targetNodeName} missed: {injectedReason}";
                return false;
            }

            reason = $"probe {sourceNodeName} -> {targetNodeName} ack";
            return true;
        }
    }

    private void MutateNode(string nodeName, Action<NodeState> mutation)
    {
        lock (_lock)
        {
            mutation(GetNodeLocked(nodeName));
        }
    }

    private NodeState GetNodeLocked(string nodeName) =>
        _nodes.TryGetValue(nodeName, out var node)
            ? node
            : throw new InvalidOperationException($"No in-process node registered for '{nodeName}'.");

    public readonly record struct DispatchPlan(
        IMessageReceiver RequestReceiver,
        IResponseReceiver ResponseReceiver,
        TimeSpan? Delay,
        Exception? ResponseError,
        string? DroppedResponseReason,
        TimeSpan? DroppedResponseReplayDelay,
        TimeSpan? DuplicateResponseDelay);

    private sealed class NodeState
    {
        public NodeState(
            IMessageReceiver requestReceiver,
            IResponseReceiver responseReceiver)
        {
            RequestReceiver = requestReceiver;
            ResponseReceiver = responseReceiver;
        }

        public IMessageReceiver RequestReceiver { get; }

        public IResponseReceiver ResponseReceiver { get; }

        public TimeSpan? NextDelay { get; set; }

        public Exception? NextResponseError { get; set; }

        public string? NextDroppedResponseReason { get; set; }

        public TimeSpan? NextDroppedResponseReplayDelay { get; set; }

        public TimeSpan? NextDuplicateResponseDelay { get; set; }

        public bool IsProbeReachable { get; set; } = true;

        public string? NextProbeFailureReason { get; set; }
    }
}
