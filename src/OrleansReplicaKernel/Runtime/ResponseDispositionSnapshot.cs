namespace OrleansReplicaKernel.Runtime;

public sealed record ResponseDispositionSnapshot(
    int AcceptedResponses,
    int LateResponses,
    int StaleResponses,
    int DuplicateResponses,
    int PendingResponses,
    int TrackedSourceRequests,
    int CompletedTargetRequests);
