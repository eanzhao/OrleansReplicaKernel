using OrleansReplicaKernel.Invocation;

namespace OrleansReplicaKernel.Streaming;

internal sealed class StreamBatchDeliveryInvokable : IInvokable
{
    public StreamBatchDeliveryInvokable(StreamBatchEnvelope envelope)
    {
        Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
    }

    public StreamBatchEnvelope Envelope { get; }

    public string InterfaceName => nameof(IAsyncStreamSubscriptionObserver<object>);

    public string InterfaceCompatibilityFamily => nameof(IAsyncStreamSubscriptionObserver<object>);

    public int InterfaceVersion => 1;

    public string MethodName => "OnNextBatchAsync";

    public async ValueTask<object?> InvokeAsync(object target, CancellationToken cancellationToken)
    {
        var streamRuntime = Runtime.ActivationExecutionContext.CurrentStreamRuntime as IStreamBatchDeliveryRuntime
            ?? throw new InvalidOperationException("Stream delivery requires an active stream runtime.");
        await streamRuntime.DeliverBatchAsync(target, Envelope, cancellationToken);
        return null;
    }
}
