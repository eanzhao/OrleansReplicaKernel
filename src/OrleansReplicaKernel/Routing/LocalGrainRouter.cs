using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Messaging;

namespace OrleansReplicaKernel.Routing;

public sealed class LocalGrainRouter : IGrainRouter
{
    private readonly string _localNodeName;
    private readonly IGrainLocator _locator;

    public LocalGrainRouter(
        string localNodeName,
        IGrainLocator locator)
    {
        _localNodeName = localNodeName;
        _locator = locator;
    }

    public GrainAddress Route(InvocationMessage message)
    {
        var address = _locator.Locate(message.Target.GrainId);
        if (address.NodeName != _localNodeName)
        {
            TraceLog.Write(
                "routing",
                $"{message.Target.GrainId} resolved to remote owner {address.NodeName}");
            return address;
        }

        TraceLog.Write("routing", $"{message.Target.GrainId} resolved to local node {address.NodeName}");
        return address;
    }
}
