using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Streaming;

public sealed record StreamSubscriptionState(
    Guid SubscriptionId,
    GrainId SubscriberGrainId,
    long NextSequenceToken,
    DateTimeOffset UpdatedUtc);
