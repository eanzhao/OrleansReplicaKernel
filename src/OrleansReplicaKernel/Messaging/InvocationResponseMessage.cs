namespace OrleansReplicaKernel.Messaging;

public sealed record InvocationResponseMessage(
    Guid RequestId,
    Guid AttemptId,
    string ResponderNodeName,
    object? Result,
    Exception? Error);
