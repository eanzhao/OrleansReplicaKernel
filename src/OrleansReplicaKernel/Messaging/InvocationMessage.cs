using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;

namespace OrleansReplicaKernel.Messaging;

public sealed record InvocationMessage(
    Guid RequestId,
    string SourceNodeName,
    GrainAddress Target,
    IInvokable Invokable);
