using OrleansReplicaKernel.Messaging;

namespace OrleansReplicaKernel.Runtime;

public interface IMessageTransport
{
    ValueTask<InvocationResponseMessage> SendAsync(
        InvocationMessage message,
        CancellationToken cancellationToken = default);
}
