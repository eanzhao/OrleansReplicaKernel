namespace OrleansReplicaKernel.Identity;

public static class CallbackTargetIdentity
{
    private const string CallbackPrefix = "$callback:";

    public static GrainId Create(string callbackType, string nodeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackType);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);

        return new GrainId(
            $"{CallbackPrefix}{callbackType}",
            $"{nodeName}/{Guid.NewGuid():N}");
    }

    public static bool IsCallback(GrainId grainId)
        => grainId.GrainType.StartsWith(CallbackPrefix, StringComparison.Ordinal);

    public static bool TryGetNodeName(GrainId grainId, out string nodeName)
    {
        if (!IsCallback(grainId))
        {
            nodeName = string.Empty;
            return false;
        }

        var separatorIndex = grainId.Key.IndexOf('/', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            nodeName = string.Empty;
            return false;
        }

        nodeName = grainId.Key[..separatorIndex];
        return true;
    }
}
