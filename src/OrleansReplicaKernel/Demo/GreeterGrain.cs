using OrleansReplicaKernel.App;

namespace OrleansReplicaKernel.Demo;

public sealed partial class GreeterGrain : IGreeterGrain
{
    public Task<string> GreetAsync(string name, CancellationToken cancellationToken = default)
    {
        TraceLog.Write("grain", $"GreeterGrain handle GreetAsync(\"{name}\")");
        return Task.FromResult($"hello:{name}");
    }
}
