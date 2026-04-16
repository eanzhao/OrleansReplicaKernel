namespace OrleansReplicaKernel.Identity;

public static class CallbackTargetIdentity
{
    private const string CallbackPrefix = "$callback:";
    private const char RouteSeparator = '>';

    public static GrainId Create(string callbackType, string nodeName)
        => Create(callbackType, nodeName, nodeName);

    public static GrainId Create(
        string callbackType,
        string routingNodeName,
        string executionNodeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackType);
        ArgumentException.ThrowIfNullOrWhiteSpace(routingNodeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionNodeName);

        var nodeDescriptor = string.Equals(routingNodeName, executionNodeName, StringComparison.Ordinal)
            ? routingNodeName
            : routingNodeName + RouteSeparator + executionNodeName;

        return new GrainId(
            $"{CallbackPrefix}{callbackType}",
            $"{nodeDescriptor}/{Guid.NewGuid():N}");
    }

    public static bool IsCallback(GrainId grainId)
        => grainId.GrainType.StartsWith(CallbackPrefix, StringComparison.Ordinal);

    public static bool TryGetNodeName(GrainId grainId, out string nodeName)
        => TryGetRoutingNodeName(grainId, out nodeName);

    public static bool TryGetRoutingNodeName(GrainId grainId, out string nodeName)
    {
        if (!TryParseNodeNames(grainId, out nodeName, out _))
        {
            nodeName = string.Empty;
            return false;
        }

        return true;
    }

    public static bool TryGetExecutionNodeName(GrainId grainId, out string nodeName)
    {
        if (!TryParseNodeNames(grainId, out _, out nodeName))
        {
            nodeName = string.Empty;
            return false;
        }

        return true;
    }

    private static bool TryParseNodeNames(
        GrainId grainId,
        out string routingNodeName,
        out string executionNodeName)
    {
        if (!IsCallback(grainId))
        {
            routingNodeName = string.Empty;
            executionNodeName = string.Empty;
            return false;
        }

        var separatorIndex = grainId.Key.IndexOf('/', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            routingNodeName = string.Empty;
            executionNodeName = string.Empty;
            return false;
        }

        var nodeDescriptor = grainId.Key[..separatorIndex];
        var routeSeparatorIndex = nodeDescriptor.IndexOf(RouteSeparator, StringComparison.Ordinal);
        if (routeSeparatorIndex <= 0 || routeSeparatorIndex == nodeDescriptor.Length - 1)
        {
            routingNodeName = nodeDescriptor;
            executionNodeName = nodeDescriptor;
            return true;
        }

        routingNodeName = nodeDescriptor[..routeSeparatorIndex];
        executionNodeName = nodeDescriptor[(routeSeparatorIndex + 1)..];
        return true;
    }
}
