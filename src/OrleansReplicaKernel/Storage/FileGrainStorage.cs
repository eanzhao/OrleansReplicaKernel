using System.Text.Json;
using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Storage;

public sealed class FileGrainStorage : IGrainStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly string _lockPath;

    public FileGrainStorage(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = Path.GetFullPath(path);
        _lockPath = _path + ".lock";

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async ValueTask ReadStateAsync<TState>(
        string stateName,
        GrainId grainId,
        IGrainState<TState> grainState,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateName);
        ArgumentNullException.ThrowIfNull(grainState);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var document = await ReadDocumentCoreAsync(cancellationToken);
                var key = new GrainStorageRecordKey(grainId, stateName);
                if (!document.TryGetRecord(key, out var record))
                {
                    grainState.State = StateValueFactory.Create<TState>();
                    grainState.ETag = null;
                    grainState.RecordExists = false;
                    return;
                }

                grainState.State = DeserializeState<TState>(record.State);
                grainState.ETag = record.ETag;
                grainState.RecordExists = true;
                return;
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }
    }

    public async ValueTask WriteStateAsync<TState>(
        string stateName,
        GrainId grainId,
        IGrainState<TState> grainState,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateName);
        ArgumentNullException.ThrowIfNull(grainState);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var writeLock = await AcquireWriteLockAsync(cancellationToken);
                var document = await ReadDocumentCoreAsync(cancellationToken);
                var key = new GrainStorageRecordKey(grainId, stateName);

                if (document.TryGetRecord(key, out var existing))
                {
                    EnsureExpectedETag(grainId, stateName, grainState.ETag, existing.ETag);
                }
                else if (grainState.ETag is not null)
                {
                    throw new InconsistentStateException(grainId, stateName, grainState.ETag, actualETag: null);
                }

                var nextETag = Guid.NewGuid().ToString("N");
                document = document.WithRecord(new GrainStorageFileRecord(
                    grainId,
                    stateName,
                    nextETag,
                    JsonSerializer.SerializeToElement(grainState.State, JsonOptions)));

                await WriteDocumentCoreAsync(document, cancellationToken);

                grainState.ETag = nextETag;
                grainState.RecordExists = true;
                return;
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }
    }

    public async ValueTask ClearStateAsync<TState>(
        string stateName,
        GrainId grainId,
        IGrainState<TState> grainState,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateName);
        ArgumentNullException.ThrowIfNull(grainState);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var writeLock = await AcquireWriteLockAsync(cancellationToken);
                var document = await ReadDocumentCoreAsync(cancellationToken);
                var key = new GrainStorageRecordKey(grainId, stateName);

                if (document.TryGetRecord(key, out var existing))
                {
                    EnsureExpectedETag(grainId, stateName, grainState.ETag, existing.ETag);
                    document = document.WithoutRecord(key);
                    await WriteDocumentCoreAsync(document, cancellationToken);
                }

                grainState.State = StateValueFactory.Create<TState>();
                grainState.ETag = null;
                grainState.RecordExists = false;
                return;
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }
    }

    private async ValueTask<GrainStorageFileDocument> ReadDocumentCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return GrainStorageFileDocument.Empty;
        }

        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous);

        if (stream.Length == 0)
        {
            return GrainStorageFileDocument.Empty;
        }

        return await JsonSerializer.DeserializeAsync<GrainStorageFileDocument>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? throw new InvalidOperationException($"Grain storage file '{_path}' contains invalid JSON.");
    }

    private async ValueTask WriteDocumentCoreAsync(
        GrainStorageFileDocument document,
        CancellationToken cancellationToken)
    {
        var tempPath = _path + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async Task<FileStream> AcquireWriteLockAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }
    }

    private static TState DeserializeState<TState>(JsonElement state)
    {
        var value = state.Deserialize<TState>(JsonOptions);
        return value is null
            ? StateValueFactory.Create<TState>()
            : value;
    }

    private static void EnsureExpectedETag(
        GrainId grainId,
        string stateName,
        string? expectedETag,
        string? actualETag)
    {
        if (string.Equals(expectedETag, actualETag, StringComparison.Ordinal))
        {
            return;
        }

        throw new InconsistentStateException(grainId, stateName, expectedETag, actualETag);
    }

    private readonly record struct GrainStorageRecordKey(GrainId GrainId, string StateName);

    private sealed record GrainStorageFileDocument(GrainStorageFileRecord[] Records)
    {
        public static GrainStorageFileDocument Empty { get; } = new([]);

        public bool TryGetRecord(GrainStorageRecordKey key, out GrainStorageFileRecord record)
        {
            foreach (var current in Records)
            {
                if (current.GrainId == key.GrainId
                    && string.Equals(current.StateName, key.StateName, StringComparison.Ordinal))
                {
                    record = current;
                    return true;
                }
            }

            record = default!;
            return false;
        }

        public GrainStorageFileDocument WithRecord(GrainStorageFileRecord record)
        {
            var records = Records
                .Where(item =>
                    item.GrainId != record.GrainId
                    || !string.Equals(item.StateName, record.StateName, StringComparison.Ordinal))
                .Append(record)
                .OrderBy(item => item.GrainId.ToString(), StringComparer.Ordinal)
                .ThenBy(item => item.StateName, StringComparer.Ordinal)
                .ToArray();
            return new GrainStorageFileDocument(records);
        }

        public GrainStorageFileDocument WithoutRecord(GrainStorageRecordKey key)
        {
            var records = Records
                .Where(item =>
                    item.GrainId != key.GrainId
                    || !string.Equals(item.StateName, key.StateName, StringComparison.Ordinal))
                .OrderBy(item => item.GrainId.ToString(), StringComparer.Ordinal)
                .ThenBy(item => item.StateName, StringComparer.Ordinal)
                .ToArray();
            return new GrainStorageFileDocument(records);
        }
    }

    private sealed record GrainStorageFileRecord(
        GrainId GrainId,
        string StateName,
        string ETag,
        JsonElement State);
}
