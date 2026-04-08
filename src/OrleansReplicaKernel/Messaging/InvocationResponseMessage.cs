namespace OrleansReplicaKernel.Messaging;

public sealed record InvocationResponseMessage(
    Guid RequestId,
    string ResponderNodeName,
    object? Result,
    Exception? Error);
