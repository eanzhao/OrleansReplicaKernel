namespace OrleansReplicaKernel.Demo;

public interface IImplicitStreamPublisherGrain
{
    Task PublishAsync(string value, CancellationToken cancellationToken = default);
}
