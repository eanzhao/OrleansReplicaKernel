using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Storage;

namespace OrleansReplicaKernel.Demo;

public sealed partial class LifecycleProbeGrain : ILifecycleProbeGrain, IGrainLifecycleParticipant
{
    private readonly IPersistentState<LifecycleProbeState> _state;

    public LifecycleProbeGrain(
        [PersistentState("lifecycle")] IPersistentState<LifecycleProbeState> state)
    {
        _state = state;
    }

    public async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        _state.State.ActivationCount++;
        await _state.WriteStateAsync(cancellationToken);
        TraceLog.Write("grain", $"LifecycleProbeGrain activate count={_state.State.ActivationCount}");
    }

    public async ValueTask OnDeactivateAsync(
        ActivationDeactivationReason reason,
        CancellationToken cancellationToken)
    {
        _state.State.DeactivationCount++;
        _state.State.LastDeactivationReason = reason.ToString();
        await _state.WriteStateAsync(cancellationToken);
        TraceLog.Write(
            "grain",
            $"LifecycleProbeGrain deactivate count={_state.State.DeactivationCount} reason={reason}");
    }

    public Task<int> GetActivationCountAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_state.State.ActivationCount);

    public Task<int> GetDeactivationCountAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_state.State.DeactivationCount);

    public Task<string> GetLastDeactivationReasonAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_state.State.LastDeactivationReason);
}

public sealed class LifecycleProbeState
{
    public int ActivationCount { get; set; }

    public int DeactivationCount { get; set; }

    public string LastDeactivationReason { get; set; } = "<none>";
}
