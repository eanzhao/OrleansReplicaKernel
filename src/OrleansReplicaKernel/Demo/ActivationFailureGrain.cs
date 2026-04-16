using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Storage;

namespace OrleansReplicaKernel.Demo;

public sealed partial class ActivationFailureGrain : IActivationFailureGrain, IGrainLifecycleParticipant
{
    private readonly IPersistentState<ActivationFailureState> _state;

    public ActivationFailureGrain(
        [PersistentState("activation-failure")] IPersistentState<ActivationFailureState> state)
    {
        _state = state;
    }

    public async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        if (!_state.State.HasFailedActivation)
        {
            _state.State.HasFailedActivation = true;
            await _state.WriteStateAsync(cancellationToken);
            throw new InvalidOperationException("Planned activation failure.");
        }

        _state.State.ActivationCount++;
        await _state.WriteStateAsync(cancellationToken);
        TraceLog.Write("grain", $"ActivationFailureGrain activate count={_state.State.ActivationCount}");
    }

    public ValueTask OnDeactivateAsync(
        ActivationDeactivationReason reason,
        CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public Task<int> GetActivationCountAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_state.State.ActivationCount);
}

public sealed class ActivationFailureState
{
    public int ActivationCount { get; set; }

    public bool HasFailedActivation { get; set; }
}
