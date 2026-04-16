namespace OrleansReplicaKernel.Runtime;

public enum ActivationDeactivationReason
{
    Explicit = 0,
    IdleCollection = 1,
    Handoff = 2,
    Shutdown = 3,
    ActivationFailed = 4,
}
