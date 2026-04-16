using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Versioning;

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
        if (CallbackTargetIdentity.TryGetNodeName(message.Target.GrainId, out var callbackNodeName))
        {
            TraceLog.Write(
                "routing",
                $"{message.Target.GrainId} resolved to callback target on {callbackNodeName}");
            return new GrainAddress(callbackNodeName, message.Target.GrainId, OwnerVersion: 0);
        }

        if (SystemTargetId.IsSystemTarget(message.Target.GrainId))
        {
            var systemTargetId = SystemTargetId.FromGrainId(message.Target.GrainId);
            TraceLog.Write(
                "routing",
                $"{message.Target.GrainId} resolved to system target on {systemTargetId.NodeName}");
            return new GrainAddress(systemTargetId.NodeName, message.Target.GrainId, OwnerVersion: 0);
        }

        var requestedInterface = GrainInterfaceVersionDescriptor.FromInvocation(
            message.Target.GrainId,
            message.Invokable);
        var address = _locator.Locate(message.Target.GrainId, requestedInterface);
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
