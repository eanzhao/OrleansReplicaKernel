namespace OrleansReplicaKernel.Runtime;

public sealed class InProcessNodeRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, NodeState> _nodes = new();

    public void Register(string nodeName, IMessageReceiver receiver)
    {
        lock (_lock)
        {
            _nodes.Add(nodeName, new NodeState(receiver));
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
                return;
            }
        }

        throw new InvalidOperationException($"No in-process node registered for '{nodeName}'.");
    }

    public DispatchPlan PrepareDispatch(string nodeName)
    {
        lock (_lock)
        {
            if (!_nodes.TryGetValue(nodeName, out var node))
            {
                throw new InvalidOperationException($"No in-process node registered for '{nodeName}'.");
            }

            var delay = node.NextDelay;
            node.NextDelay = null;

            var nextResponseError = node.NextResponseError;
            node.NextResponseError = null;

            var nextDroppedResponseReason = node.NextDroppedResponseReason;
            node.NextDroppedResponseReason = null;

            return new DispatchPlan(node.Receiver, delay, nextResponseError, nextDroppedResponseReason);
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
        IMessageReceiver Receiver,
        TimeSpan? Delay,
        Exception? ResponseError,
        string? DroppedResponseReason);

    private sealed class NodeState
    {
        public NodeState(IMessageReceiver receiver)
        {
            Receiver = receiver;
        }

        public IMessageReceiver Receiver { get; }

        public TimeSpan? NextDelay { get; set; }

        public Exception? NextResponseError { get; set; }

        public string? NextDroppedResponseReason { get; set; }

        public bool IsProbeReachable { get; set; } = true;

        public string? NextProbeFailureReason { get; set; }
    }
}
