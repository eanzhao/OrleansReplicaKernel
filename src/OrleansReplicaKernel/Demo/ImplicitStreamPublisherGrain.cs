using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Streaming;

namespace OrleansReplicaKernel.Demo;

public sealed partial class ImplicitStreamPublisherGrain : IImplicitStreamPublisherGrain
{
    public async Task PublishAsync(string value, CancellationToken cancellationToken = default)
    {
        await GrainStreams
            .GetStream<string>("memory", "implicit", GetStreamKey())
            .OnNextAsync(value, cancellationToken);
    }

    private static string GetStreamKey()
        => ActivationExecutionContext.CurrentGrainId?.Key
            ?? throw new InvalidOperationException("Stream publisher requires an active grain id.");
}
