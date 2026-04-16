namespace OrleansReplicaKernel.Runtime;

public sealed class NoOpFailureDetector : IFailureDetector
{
    public void ReportFailure(string nodeName, string reason)
    {
    }

    public void ReportSuccess(string nodeName, string reason)
    {
    }
}
