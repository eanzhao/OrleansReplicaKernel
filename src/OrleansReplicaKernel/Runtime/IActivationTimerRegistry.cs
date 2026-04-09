namespace OrleansReplicaKernel.Runtime;

public interface IActivationTimerRegistry
{
    ValueTask<IActivationTimerHandle> RegisterTimerAsync(
        string timerName,
        TimeSpan dueTime,
        TimeSpan? period,
        Func<CancellationToken, ValueTask> callback);
}
