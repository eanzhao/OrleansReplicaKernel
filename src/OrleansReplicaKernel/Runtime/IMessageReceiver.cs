using OrleansReplicaKernel.Messaging;

namespace OrleansReplicaKernel.Runtime;

public interface IMessageReceiver
{
    ValueTask<InvocationResponseMessage> ReceiveAsync(
        InvocationMessage message,
        CancellationToken cancellationToken = default);
}
