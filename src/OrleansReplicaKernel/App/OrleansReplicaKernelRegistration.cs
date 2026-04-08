using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;

namespace OrleansReplicaKernel.App;

internal sealed record OrleansReplicaKernelRegistration(
    string GrainType,
    Func<IInvocationRuntime, GrainId, object> ReferenceFactory);
