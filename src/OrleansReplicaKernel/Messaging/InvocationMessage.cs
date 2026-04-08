using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;

namespace OrleansReplicaKernel.Messaging;

public sealed record InvocationMessage(
    Guid RequestId,
    Guid AttemptId,
    int AttemptSequence,
    string SourceNodeName,
    GrainAddress Target,
    IInvokable Invokable);
