namespace OrleansReplicaKernel.Demo;

public interface IStreamPublisherGrain
{
    Task PublishAsync(string value, CancellationToken cancellationToken = default);
}
