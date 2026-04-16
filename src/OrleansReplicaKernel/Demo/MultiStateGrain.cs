using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Storage;

namespace OrleansReplicaKernel.Demo;

public sealed partial class MultiStateGrain : IMultiStateGrain
{
    private readonly IPersistentState<NamedTextState> _primaryState;
    private readonly IPersistentState<NamedTextState> _secondaryState;

    public MultiStateGrain(
        [PersistentState("primary")] IPersistentState<NamedTextState> primaryState,
        [PersistentState("secondary", "secondary")] IPersistentState<NamedTextState> secondaryState)
    {
        _primaryState = primaryState;
        _secondaryState = secondaryState;
    }

    public async Task SetPrimaryAsync(string value, CancellationToken cancellationToken = default)
    {
        _primaryState.State.Value = value;
        await _primaryState.WriteStateAsync(cancellationToken);
        TraceLog.Write("grain", $"MultiStateGrain set primary='{value}'");
    }

    public async Task SetSecondaryAsync(string value, CancellationToken cancellationToken = default)
    {
        _secondaryState.State.Value = value;
        await _secondaryState.WriteStateAsync(cancellationToken);
        TraceLog.Write("grain", $"MultiStateGrain set secondary='{value}'");
    }

    public Task<string> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult($"{_primaryState.State.Value}|{_secondaryState.State.Value}");
}

public sealed class NamedTextState
{
    public string Value { get; set; } = string.Empty;
}
