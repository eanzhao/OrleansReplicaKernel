namespace OrleansReplicaKernel.Runtime;

public interface IFailureDetector
{
    void ReportFailure(string nodeName, string reason);

    void ReportSuccess(string nodeName, string reason);
}
