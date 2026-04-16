namespace OrleansReplicaKernel.Streaming;

internal interface IStreamBatchDeliveryRuntime
{
    ValueTask DeliverBatchAsync(
        object target,
        StreamBatchEnvelope envelope,
        CancellationToken cancellationToken);
}
