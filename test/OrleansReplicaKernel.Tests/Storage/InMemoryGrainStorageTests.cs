using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Storage;

namespace OrleansReplicaKernel.Tests.Storage;

public sealed class InMemoryGrainStorageTests
{
    private readonly InMemoryGrainStorage _storage = new();
    private readonly GrainId _grainId = new("testGrain", "key1");

    [Fact]
    public async Task ReadState_NonExistent_SetsRecordExistsFalse()
    {
        var state = new TestGrainState<CounterState>();

        await _storage.ReadStateAsync("counter", _grainId, state);

        Assert.False(state.RecordExists);
        Assert.Null(state.ETag);
    }

    [Fact]
    public async Task WriteAndRead_RoundTrips()
    {
        var state = new TestGrainState<CounterState> { State = new CounterState { Value = 42 } };

        await _storage.WriteStateAsync("counter", _grainId, state);
        Assert.True(state.RecordExists);
        Assert.NotNull(state.ETag);

        var readState = new TestGrainState<CounterState>();
        await _storage.ReadStateAsync("counter", _grainId, readState);

        Assert.True(readState.RecordExists);
        Assert.Equal(42, readState.State.Value);
        Assert.Equal(state.ETag, readState.ETag);
    }

    [Fact]
    public async Task WriteState_UpdatesETag()
    {
        var state = new TestGrainState<CounterState> { State = new CounterState { Value = 1 } };

        await _storage.WriteStateAsync("counter", _grainId, state);
        var firstETag = state.ETag;

        state.State.Value = 2;
        await _storage.WriteStateAsync("counter", _grainId, state);
        var secondETag = state.ETag;

        Assert.NotEqual(firstETag, secondETag);
    }

    [Fact]
    public async Task WriteState_StaleETag_ThrowsInconsistentState()
    {
        var state = new TestGrainState<CounterState> { State = new CounterState { Value = 1 } };
        await _storage.WriteStateAsync("counter", _grainId, state);

        state.ETag = "stale-etag";
        state.State.Value = 2;

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => _storage.WriteStateAsync("counter", _grainId, state).AsTask());
    }

    [Fact]
    public async Task ClearState_RemovesRecord()
    {
        var state = new TestGrainState<CounterState> { State = new CounterState { Value = 42 } };
        await _storage.WriteStateAsync("counter", _grainId, state);

        await _storage.ClearStateAsync("counter", _grainId, state);

        Assert.False(state.RecordExists);
        Assert.Null(state.ETag);

        var readState = new TestGrainState<CounterState>();
        await _storage.ReadStateAsync("counter", _grainId, readState);
        Assert.False(readState.RecordExists);
    }

    [Fact]
    public async Task DifferentGrainIds_AreIsolated()
    {
        var grainId2 = new GrainId("testGrain", "key2");

        var state1 = new TestGrainState<CounterState> { State = new CounterState { Value = 1 } };
        var state2 = new TestGrainState<CounterState> { State = new CounterState { Value = 2 } };

        await _storage.WriteStateAsync("counter", _grainId, state1);
        await _storage.WriteStateAsync("counter", grainId2, state2);

        var read1 = new TestGrainState<CounterState>();
        var read2 = new TestGrainState<CounterState>();
        await _storage.ReadStateAsync("counter", _grainId, read1);
        await _storage.ReadStateAsync("counter", grainId2, read2);

        Assert.Equal(1, read1.State.Value);
        Assert.Equal(2, read2.State.Value);
    }

    [Fact]
    public async Task DifferentStateNames_AreIsolated()
    {
        var state1 = new TestGrainState<CounterState> { State = new CounterState { Value = 10 } };
        var state2 = new TestGrainState<CounterState> { State = new CounterState { Value = 20 } };

        await _storage.WriteStateAsync("stateA", _grainId, state1);
        await _storage.WriteStateAsync("stateB", _grainId, state2);

        var read1 = new TestGrainState<CounterState>();
        var read2 = new TestGrainState<CounterState>();
        await _storage.ReadStateAsync("stateA", _grainId, read1);
        await _storage.ReadStateAsync("stateB", _grainId, read2);

        Assert.Equal(10, read1.State.Value);
        Assert.Equal(20, read2.State.Value);
    }

    [Fact]
    public async Task ClearState_NonExistent_DoesNotThrow()
    {
        var state = new TestGrainState<CounterState>();
        await _storage.ClearStateAsync("counter", _grainId, state);

        Assert.False(state.RecordExists);
    }

    private sealed class TestGrainState<TState> : IGrainState<TState>
        where TState : new()
    {
        public TState State { get; set; } = new();
        public string? ETag { get; set; }
        public bool RecordExists { get; set; }
    }

    private sealed class CounterState
    {
        public int Value { get; set; }
    }
}
