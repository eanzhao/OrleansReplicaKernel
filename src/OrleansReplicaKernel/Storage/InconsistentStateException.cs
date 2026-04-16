using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Storage;

public sealed class InconsistentStateException : Exception
{
    public InconsistentStateException(
        GrainId grainId,
        string stateName,
        string? expectedETag,
        string? actualETag)
        : base(
            $"Persistent state '{stateName}' for grain '{grainId}' failed an ETag check. Expected '{expectedETag ?? "<null>"}', actual '{actualETag ?? "<null>"}'.")
    {
        GrainId = grainId;
        StateName = stateName;
        ExpectedETag = expectedETag;
        ActualETag = actualETag;
    }

    public GrainId GrainId { get; }

    public string StateName { get; }

    public string? ExpectedETag { get; }

    public string? ActualETag { get; }
}
