using OrleansReplicaKernel.Routing;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.App;

public sealed record OrleansReplicaKernelRuntimeCheckpoint(
    OrleansReplicaKernelMembershipCheckpoint Membership,
    GrainDirectoryCheckpoint GrainDirectory,
    IReadOnlyList<ActivationDirectoryCheckpoint> ActivationDirectories);
