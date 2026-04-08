namespace OrleansReplicaKernel.Routing;

public readonly record struct ActivationLoadRecord(
    string NodeName,
    int ActivationCount);
