namespace OrleansReplicaKernel.Routing;

public sealed class RoundRobinGatewaySelector
{
    private readonly string[] _gatewayNodeNames;
    private int _nextGatewayIndex = -1;

    public RoundRobinGatewaySelector(IEnumerable<string> gatewayNodeNames)
    {
        ArgumentNullException.ThrowIfNull(gatewayNodeNames);

        _gatewayNodeNames = gatewayNodeNames
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (_gatewayNodeNames.Length == 0)
        {
            throw new InvalidOperationException("At least one gateway node must be configured.");
        }
    }

    public string SelectNextGateway()
    {
        var index = Interlocked.Increment(ref _nextGatewayIndex);
        return _gatewayNodeNames[(index & 0x7FFF_FFFF) % _gatewayNodeNames.Length];
    }

    public IReadOnlyList<string> GetGatewayNodeNames()
        => _gatewayNodeNames;
}
