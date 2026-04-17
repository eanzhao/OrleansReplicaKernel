namespace OrleansReplicaKernel.Demo;

public interface IImplicitSubscriberGrain
{
    Task<string> GetReceivedSnapshotAsync(CancellationToken cancellationToken = default);

    Task<long> GetLastSequenceTokenAsync(CancellationToken cancellationToken = default);
}
