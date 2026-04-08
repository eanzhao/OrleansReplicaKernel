namespace OrleansReplicaKernel.Runtime;

public sealed class InProcessNodeRegistry
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

    public void DelayNextRequest(string nodeName, TimeSpan delay)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(nodeName, out var node))
            {
                node.NextDelay = delay;
                return;
            }
        }

        throw new InvalidOperationException($"No in-process node registered for '{nodeName}'.");
    }

    public void FailNextRequestWithResponse(string nodeName, Exception error)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(nodeName, out var node))
            {
                node.NextResponseError = error;
                return;
            }
        }

        throw new InvalidOperationException($"No in-process node registered for '{nodeName}'.");
    }

    public void DropNextResponse(string nodeName, string reason)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(nodeName, out var node))
            {
                node.NextDroppedResponseReason = reason;
                node.NextDroppedResponseReplayDelay = null;
                return;
            }
        }

        throw new InvalidOperationException($"No in-process node registered for '{nodeName}'.");
    }

    public void DropNextResponseAndReplayLater(string nodeName, TimeSpan replayDelay, string reason)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(nodeName, out var node))
            {
                node.NextDroppedResponseReason = reason;
                node.NextDroppedResponseReplayDelay = replayDelay;
                return;
            }
        }

        throw new InvalidOperationException($"No in-process node registered for '{nodeName}'.");
    }

    public void DuplicateNextResponse(string nodeName, TimeSpan duplicateDelay)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(nodeName, out var node))
            {
                node.NextDuplicateResponseDelay = duplicateDelay;
                return;
            }
        }

        throw new InvalidOperationException($"No in-process node registered for '{nodeName}'.");
    }

    public DispatchPlan PrepareDispatch(string sourceNodeName, string targetNodeName)
    {
        lock (_lock)
        {
            if (!_nodes.TryGetValue(targetNodeName, out var targetNode))
            {
                throw new InvalidOperationException($"No in-process node registered for '{targetNodeName}'.");
            }

            if (!_nodes.TryGetValue(sourceNodeName, out var sourceNode))
            {
                throw new InvalidOperationException($"No in-process node registered for '{sourceNodeName}'.");
            }

            var delay = targetNode.NextDelay;
            targetNode.NextDelay = null;

            var nextResponseError = targetNode.NextResponseError;
            targetNode.NextResponseError = null;

            var nextDroppedResponseReason = targetNode.NextDroppedResponseReason;
            targetNode.NextDroppedResponseReason = null;

            var nextDroppedResponseReplayDelay = targetNode.NextDroppedResponseReplayDelay;
            targetNode.NextDroppedResponseReplayDelay = null;

            var nextDuplicateResponseDelay = targetNode.NextDuplicateResponseDelay;
            targetNode.NextDuplicateResponseDelay = null;

            return new DispatchPlan(
                targetNode.RequestReceiver,
                sourceNode.ResponseReceiver,
                delay,
                nextResponseError,
                nextDroppedResponseReason,
                nextDroppedResponseReplayDelay,
                nextDuplicateResponseDelay);
        }
    }

    public void SetProbeReachable(string nodeName, bool isReachable)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(nodeName, out var node))
            {
                node.IsProbeReachable = isReachable;
                return;
            }
        }

        throw new InvalidOperationException($"No in-process node registered for '{nodeName}'.");
    }

    public void FailNextProbe(string nodeName, string failureReason)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(nodeName, out var node))
            {
                node.NextProbeFailureReason = failureReason;
                return;
            }
        }

        throw new InvalidOperationException($"No in-process node registered for '{nodeName}'.");
    }

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

            if (node.NextProbeFailureReason is { } failureReason)
            {
                node.NextProbeFailureReason = null;
                reason = $"probe {sourceNodeName} -> {targetNodeName} missed: {failureReason}";
                return false;
            }

            reason = $"probe {sourceNodeName} -> {targetNodeName} ack";
            return true;
        }
    }

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
