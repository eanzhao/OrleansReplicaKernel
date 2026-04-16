namespace OrleansReplicaKernel.Storage;

public interface IGrainState<TState>
{
    TState State { get; set; }

    string? ETag { get; set; }

    bool RecordExists { get; set; }
}
