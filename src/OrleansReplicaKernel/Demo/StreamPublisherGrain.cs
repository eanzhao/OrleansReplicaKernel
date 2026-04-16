using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Streaming;

namespace OrleansReplicaKernel.Demo;

public sealed partial class StreamPublisherGrain : IStreamPublisherGrain
{
    public async Task PublishAsync(string value, CancellationToken cancellationToken = default)
    {
        await GrainStreams
            .GetStream<string>("memory", "demo", GetStreamKey())
            .OnNextAsync(value, cancellationToken);
    }

    private static string GetStreamKey()
        => ActivationExecutionContext.CurrentGrainId?.Key
            ?? throw new InvalidOperationException("Stream publisher requires an active grain id.");
}
