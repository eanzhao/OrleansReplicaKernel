namespace OrleansReplicaKernel.Demo;

public interface IFailingStreamPublisherGrain
{
    Task PublishAsync(string value, CancellationToken cancellationToken = default);
}
