namespace OrleansReplicaKernel.Runtime;

public interface IActivationTimerHandle : IAsyncDisposable
{
    string TimerName { get; }
}
