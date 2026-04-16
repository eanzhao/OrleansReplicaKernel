using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Storage;

public interface IGrainStorage
{
    ValueTask ReadStateAsync<TState>(
        string stateName,
        GrainId grainId,
        IGrainState<TState> grainState,
        CancellationToken cancellationToken = default);

    ValueTask WriteStateAsync<TState>(
        string stateName,
        GrainId grainId,
        IGrainState<TState> grainState,
        CancellationToken cancellationToken = default);

    ValueTask ClearStateAsync<TState>(
        string stateName,
        GrainId grainId,
        IGrainState<TState> grainState,
        CancellationToken cancellationToken = default);
}
