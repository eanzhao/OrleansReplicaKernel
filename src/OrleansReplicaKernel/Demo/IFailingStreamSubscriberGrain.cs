namespace OrleansReplicaKernel.Demo;

public interface IFailingStreamSubscriberGrain
{
    Task<int> GetDeliveryAttemptCountAsync(CancellationToken cancellationToken = default);
}
