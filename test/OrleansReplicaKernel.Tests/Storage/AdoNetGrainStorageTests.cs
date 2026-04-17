using Microsoft.Data.Sqlite;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Storage;

namespace OrleansReplicaKernel.Tests.Storage;

public sealed class AdoNetGrainStorageTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly AdoNetGrainStorage _storage;
    private readonly GrainId _grainId = new("testGrain", "key1");

    public AdoNetGrainStorageTests()
    {
        _keepAlive = new SqliteConnection("Data Source=InMemoryAdoNetTest;Mode=Memory;Cache=Shared");
        _keepAlive.Open();
        _storage = new AdoNetGrainStorage(
            () => new SqliteConnection("Data Source=InMemoryAdoNetTest;Mode=Memory;Cache=Shared"));
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
    }

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
    public async Task AutoCreatesTable()
    {
        var state = new TestGrainState<CounterState> { State = new CounterState { Value = 99 } };
        await _storage.WriteStateAsync("counter", _grainId, state);

        var readState = new TestGrainState<CounterState>();
        await _storage.ReadStateAsync("counter", _grainId, readState);

        Assert.Equal(99, readState.State.Value);
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
