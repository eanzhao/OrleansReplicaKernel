namespace OrleansReplicaKernel.Runtime;

public interface IClusterProbeService
{
    ValueTask<int> ProbePeersAsync(CancellationToken cancellationToken = default);
}
