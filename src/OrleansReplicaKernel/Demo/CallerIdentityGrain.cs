using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Demo;

public sealed partial class CallerIdentityGrain : ICallerIdentityGrain
{
    private const string GrainType = "callerIdentity";

    public Task<string> GetCurrentIdentityAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(FormatCurrentIdentity());

    public async Task<string> GetNestedCurrentIdentityAsync(
        string nestedKey,
        CancellationToken cancellationToken = default)
    {
        var runtime = ActivationExecutionContext.CurrentRuntime
            ?? throw new InvalidOperationException("No activation runtime is available for nested identity call.");
        var nested = new CallerIdentityGrainReference(runtime, new GrainId(GrainType, nestedKey));
        return await nested.GetCurrentIdentityAsync(cancellationToken);
    }

    private static string FormatCurrentIdentity()
    {
        var identity = ActivationExecutionContext.CurrentInvocationIdentity;
        if (identity is null)
        {
            return "<anonymous>";
        }

        return $"{identity.SourceKind}|{identity.Name}|auth={identity.IsAuthenticated}|thumbprint={identity.CertificateThumbprint ?? "<none>"}";
    }
}
