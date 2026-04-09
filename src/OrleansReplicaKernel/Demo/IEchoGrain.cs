namespace OrleansReplicaKernel.Demo;

public interface IEchoGrain
{
    Task<string> PingAsync(string text, CancellationToken cancellationToken = default);

    Task<string> PingSlowAsync(string text, int delayMs, CancellationToken cancellationToken = default);

    Task<string> PingWithObserverAsync(
        string text,
        IEchoObserver observer,
        CancellationToken cancellationToken = default);

    Task<int> ReentrantSelfCallAsync(int remaining, CancellationToken cancellationToken = default);

    Task ArmOneShotTimerAsync(string timerName, int delayMs, CancellationToken cancellationToken = default);

    Task<string> HoldTurnWithTimerAsync(
        string timerName,
        int holdDelayMs,
        int timerDelayMs,
        CancellationToken cancellationToken = default);

    Task<string> GetTimerSnapshotAsync(CancellationToken cancellationToken = default);
}
