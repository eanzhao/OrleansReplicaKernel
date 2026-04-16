namespace OrleansReplicaKernel.Storage;

public interface IStorage<TState>
{
    TState State { get; set; }

    string? ETag { get; }

    bool RecordExists { get; }

    ValueTask ReadStateAsync(CancellationToken cancellationToken = default);

    ValueTask WriteStateAsync(CancellationToken cancellationToken = default);

    ValueTask ClearStateAsync(CancellationToken cancellationToken = default);
}
