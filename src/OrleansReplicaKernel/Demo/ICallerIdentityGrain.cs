namespace OrleansReplicaKernel.Demo;

public interface ICallerIdentityGrain
{
    Task<string> GetCurrentIdentityAsync(CancellationToken cancellationToken = default);

    Task<string> GetNestedCurrentIdentityAsync(string nestedKey, CancellationToken cancellationToken = default);
}
