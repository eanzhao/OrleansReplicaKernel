namespace OrleansReplicaKernel.Runtime;

public interface ITransportFaultInjector
{
    void DelayNextRequest(string nodeName, TimeSpan delay);

    void FailNextRequestWithResponse(string nodeName, Exception error);

    void DropNextResponse(string nodeName, string reason);

    void DropNextResponseAndReplayLater(string nodeName, TimeSpan replayDelay, string reason);

    void DuplicateNextResponse(string nodeName, TimeSpan duplicateDelay);
}
