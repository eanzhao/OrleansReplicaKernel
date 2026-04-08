namespace OrleansReplicaKernel.Messaging;

public sealed record InvocationResponseMessage(
    Guid RequestId,
    Guid AttemptId,
    int AttemptSequence,
    string ResponderNodeName,
    object? Result,
    Exception? Error);
