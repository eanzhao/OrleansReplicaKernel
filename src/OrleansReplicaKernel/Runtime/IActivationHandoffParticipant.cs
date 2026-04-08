namespace OrleansReplicaKernel.Runtime;

public interface IActivationHandoffParticipant
{
    object? CaptureHandoffState();

    void ApplyHandoffState(object? state);
}
