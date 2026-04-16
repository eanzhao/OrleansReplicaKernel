namespace OrleansReplicaKernel.Demo;

public interface IGreeterGrain
{
    Task<string> GreetAsync(string name, CancellationToken cancellationToken = default);
}
