using OrleansReplicaKernel.Messaging;

namespace OrleansReplicaKernel.Runtime;

public interface IResponseReceiver
{
    ValueTask ReceiveResponseAsync(
        InvocationResponseMessage response,
        CancellationToken cancellationToken = default);
}
