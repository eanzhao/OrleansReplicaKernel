using System.Collections.Concurrent;
using System.Text.Json;
using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Storage;

public sealed class InMemoryGrainStorage : IGrainStorage
{
    private readonly ConcurrentDictionary<string, StoredRecord> _store = new(StringComparer.Ordinal);

    public ValueTask ReadStateAsync<TState>(
        string stateName,
        GrainId grainId,
        IGrainState<TState> grainState,
        CancellationToken cancellationToken = default)
    {
        var key = BuildKey(stateName, grainId);
        if (_store.TryGetValue(key, out var record))
        {
            grainState.State = JsonSerializer.Deserialize<TState>(record.Json)!;
            grainState.ETag = record.ETag;
            grainState.RecordExists = true;
        }
        else
        {
            grainState.RecordExists = false;
            grainState.ETag = null;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask WriteStateAsync<TState>(
        string stateName,
        GrainId grainId,
        IGrainState<TState> grainState,
        CancellationToken cancellationToken = default)
    {
        var key = BuildKey(stateName, grainId);
        var newETag = Guid.NewGuid().ToString("N");
        var json = JsonSerializer.Serialize(grainState.State);

        _store.AddOrUpdate(
            key,
            _ =>
            {
                if (grainState.ETag is not null)
                {
                    throw new InconsistentStateException(grainId, stateName, grainState.ETag, null);
                }

                return new StoredRecord(json, newETag);
            },
            (_, existing) =>
            {
                if (!string.Equals(existing.ETag, grainState.ETag, StringComparison.Ordinal))
                {
                    throw new InconsistentStateException(grainId, stateName, grainState.ETag, existing.ETag);
                }

                return new StoredRecord(json, newETag);
            });

        grainState.ETag = newETag;
        grainState.RecordExists = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearStateAsync<TState>(
        string stateName,
        GrainId grainId,
        IGrainState<TState> grainState,
        CancellationToken cancellationToken = default)
    {
        var key = BuildKey(stateName, grainId);
        _store.TryRemove(key, out _);
        grainState.ETag = null;
        grainState.RecordExists = false;
        return ValueTask.CompletedTask;
    }

    private static string BuildKey(string stateName, GrainId grainId)
        => $"{grainId.GrainType}/{grainId.Key}/{stateName}";

    private sealed record StoredRecord(string Json, string ETag);
}
