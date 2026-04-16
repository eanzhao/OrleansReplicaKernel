namespace OrleansReplicaKernel.Demo;

public interface IStreamSubscriberGrain
{
    Task<string> GetReceivedSnapshotAsync(CancellationToken cancellationToken = default);

    Task<string> GetBatchSizesSnapshotAsync(CancellationToken cancellationToken = default);

    Task<long> GetLastSequenceTokenAsync(CancellationToken cancellationToken = default);
}
