namespace OrleansReplicaKernel.Runtime;

public interface IProbeReachabilityController
{
    void SetProbeReachable(string nodeName, bool isReachable);

    void FailNextProbe(string nodeName, string reason);
}
