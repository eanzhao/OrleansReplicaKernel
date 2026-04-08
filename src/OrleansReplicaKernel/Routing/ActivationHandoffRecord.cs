using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Routing;

public sealed record ActivationHandoffRecord(
    GrainId GrainId,
    string InstanceTypeName,
    object? Payload,
    DateTimeOffset CapturedUtc);
