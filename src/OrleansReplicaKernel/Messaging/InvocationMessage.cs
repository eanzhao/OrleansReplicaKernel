using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;

namespace OrleansReplicaKernel.Messaging;

public sealed record InvocationMessage(
    Guid RequestId,
    Guid AttemptId,
    string SourceNodeName,
    GrainAddress Target,
    IInvokable Invokable);
