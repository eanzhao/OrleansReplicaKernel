using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Storage;

public sealed class PersistentState<TState> : IPersistentState<TState>, IPersistentStateParticipant
{
    private readonly IGrainStorage _grainStorage;
    private readonly GrainState<TState> _grainState = new();
    private int _isInitialized;

    public PersistentState(
        string stateName,
        GrainId grainId,
        IGrainStorage grainStorage,
        string? storageName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateName);

        StateName = stateName;
        GrainId = grainId;
        StorageName = string.IsNullOrWhiteSpace(storageName) ? null : storageName;
        _grainStorage = grainStorage ?? throw new ArgumentNullException(nameof(grainStorage));
    }

    public string StateName { get; }

    public GrainId GrainId { get; }

    public string? StorageName { get; }

    public TState State
    {
        get => _grainState.State;
        set => _grainState.State = value;
    }

    public string? ETag => _grainState.ETag;

    public bool RecordExists => _grainState.RecordExists;

    public async ValueTask ReadStateAsync(CancellationToken cancellationToken = default)
    {
        await _grainStorage.ReadStateAsync(StateName, GrainId, _grainState, cancellationToken);
        Volatile.Write(ref _isInitialized, 1);
        TraceLog.Write("storage", $"read {GrainId} state={StateName} provider={StorageName ?? "<default>"} etag={ETag ?? "<null>"}");
    }

    public async ValueTask WriteStateAsync(CancellationToken cancellationToken = default)
    {
        await _grainStorage.WriteStateAsync(StateName, GrainId, _grainState, cancellationToken);
        Volatile.Write(ref _isInitialized, 1);
        TraceLog.Write("storage", $"write {GrainId} state={StateName} provider={StorageName ?? "<default>"} etag={ETag ?? "<null>"}");
    }

    public async ValueTask ClearStateAsync(CancellationToken cancellationToken = default)
    {
        await _grainStorage.ClearStateAsync(StateName, GrainId, _grainState, cancellationToken);
        Volatile.Write(ref _isInitialized, 1);
        TraceLog.Write("storage", $"clear {GrainId} state={StateName} provider={StorageName ?? "<default>"}");
    }

    async ValueTask IPersistentStateParticipant.EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _isInitialized) == 1)
        {
            return;
        }

        await ReadStateAsync(cancellationToken);
    }
}

internal interface IPersistentStateParticipant
{
    ValueTask EnsureInitializedAsync(CancellationToken cancellationToken = default);
}
