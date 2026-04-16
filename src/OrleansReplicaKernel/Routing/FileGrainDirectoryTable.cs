using System.Text.Json;

namespace OrleansReplicaKernel.Routing;

public sealed class FileGrainDirectoryTable : IGrainDirectoryTable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly string _lockPath;

    public FileGrainDirectoryTable(string path)
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

    public async ValueTask<GrainDirectoryTableSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await ReadSnapshotCoreAsync(cancellationToken);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }
    }

    public async ValueTask<bool> WriteAsync(
        GrainDirectoryTableWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var writeLock = await AcquireWriteLockAsync(cancellationToken);
                var current = await ReadSnapshotCoreAsync(cancellationToken);
                if (current.Version != request.ExpectedVersion)
                {
                    return false;
                }

                var tempPath = _path + ".tmp." + Guid.NewGuid().ToString("N");
                try
                {
                    var document = GrainDirectoryTableFileDocument.From(
                        GrainDirectoryCheckpointHelper.Clone(request.Checkpoint),
                        current.Version + 1);

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
                    return true;
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }
    }

    private async ValueTask<GrainDirectoryTableSnapshot> ReadSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new GrainDirectoryTableSnapshot(0, GrainDirectoryCheckpoint.Empty);
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
            return new GrainDirectoryTableSnapshot(0, GrainDirectoryCheckpoint.Empty);
        }

        var document = await JsonSerializer.DeserializeAsync<GrainDirectoryTableFileDocument>(
            stream,
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException($"Grain directory table '{_path}' contains invalid JSON.");

        return new GrainDirectoryTableSnapshot(
            document.Version,
            new GrainDirectoryCheckpoint(
                document.Records
                    .OrderBy(item => item.GrainId.ToString(), StringComparer.Ordinal)
                    .ToArray()));
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

    private sealed record GrainDirectoryTableFileDocument(
        long Version,
        GrainOwnerRecord[] Records)
    {
        public static GrainDirectoryTableFileDocument From(GrainDirectoryCheckpoint checkpoint, long version)
            => new(
                version,
                checkpoint.Records.ToArray());
    }
}
