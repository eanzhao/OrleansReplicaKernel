namespace OrleansReplicaKernel.Demo;

public interface IEchoGrain
{
    Task<string> PingAsync(string text, CancellationToken cancellationToken = default);

    Task<string> PingSlowAsync(string text, int delayMs, CancellationToken cancellationToken = default);
}
