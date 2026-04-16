using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Versioning;

namespace OrleansReplicaKernel.Routing;

public sealed class GatewayGrainLocator : IGrainLocator
{
    private readonly object _lock = new();
    private readonly RoundRobinGatewaySelector _gatewaySelector;
    private readonly Dictionary<GrainId, string> _gatewayAssignments = new();
    private readonly Dictionary<GrainId, string> _invalidatedGatewayAssignments = new();

    public GatewayGrainLocator(RoundRobinGatewaySelector gatewaySelector)
    {
        _gatewaySelector = gatewaySelector ?? throw new ArgumentNullException(nameof(gatewaySelector));
    }

    public GrainAddress Locate(GrainId grainId, GrainInterfaceVersionDescriptor? requestedInterface = null)
    {
        string gatewayNodeName;

        lock (_lock)
        {
            if (!_gatewayAssignments.TryGetValue(grainId, out gatewayNodeName!))
            {
                gatewayNodeName = SelectGatewayForMiss(grainId);
                _gatewayAssignments[grainId] = gatewayNodeName;
            }
        }

        TraceLog.Write("gateway", $"route client grain {grainId} via gateway {gatewayNodeName}");
        return new GrainAddress(gatewayNodeName, grainId, OwnerVersion: 0);
    }

    public void Invalidate(GrainId grainId)
    {
        lock (_lock)
        {
            if (_gatewayAssignments.Remove(grainId, out var previousGatewayNodeName))
            {
                _invalidatedGatewayAssignments[grainId] = previousGatewayNodeName;
                TraceLog.Write("gateway", $"invalidate gateway assignment for {grainId}");
            }
        }
    }

    private string SelectGatewayForMiss(GrainId grainId)
    {
        if (!_invalidatedGatewayAssignments.TryGetValue(grainId, out var invalidatedGatewayNodeName))
        {
            return _gatewaySelector.SelectNextGateway();
        }

        var gateways = _gatewaySelector.GetGatewayNodeNames();
        if (gateways.Count == 1)
        {
            _invalidatedGatewayAssignments.Remove(grainId);
            return gateways[0];
        }

        for (var attempt = 0; attempt < gateways.Count; attempt++)
        {
            var selectedGatewayNodeName = _gatewaySelector.SelectNextGateway();
            if (!string.Equals(selectedGatewayNodeName, invalidatedGatewayNodeName, StringComparison.Ordinal))
            {
                _invalidatedGatewayAssignments.Remove(grainId);
                return selectedGatewayNodeName;
            }
        }

        _invalidatedGatewayAssignments.Remove(grainId);
        return _gatewaySelector.SelectNextGateway();
    }
}
