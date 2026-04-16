using System.Text.Json;

namespace OrleansReplicaKernel.Versioning;

public sealed class FileGrainInterfaceVersionTable : IGrainInterfaceVersionTable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly string _lockPath;

    public FileGrainInterfaceVersionTable(string path)
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

    public async ValueTask<GrainInterfaceVersionTableSnapshot> ReadAsync(CancellationToken cancellationToken = default)
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
        GrainInterfaceVersionTableWriteRequest request,
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
                    var document = GrainInterfaceVersionTableFileDocument.From(
                        InMemoryGrainInterfaceVersionTable.Clone(request.Checkpoint),
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

    private async ValueTask<GrainInterfaceVersionTableSnapshot> ReadSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new GrainInterfaceVersionTableSnapshot(0, GrainInterfaceVersionManifestCheckpoint.Empty);
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
            return new GrainInterfaceVersionTableSnapshot(0, GrainInterfaceVersionManifestCheckpoint.Empty);
        }

        var document = await JsonSerializer.DeserializeAsync<GrainInterfaceVersionTableFileDocument>(
            stream,
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException($"Grain interface version table '{_path}' contains invalid JSON.");

        return new GrainInterfaceVersionTableSnapshot(
            document.Version,
            InMemoryGrainInterfaceVersionTable.Clone(new GrainInterfaceVersionManifestCheckpoint(document.Nodes)));
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

    private sealed record GrainInterfaceVersionTableFileDocument(
        long Version,
        NodeGrainInterfaceVersionManifest[] Nodes)
    {
        public static GrainInterfaceVersionTableFileDocument From(
            GrainInterfaceVersionManifestCheckpoint checkpoint,
            long version)
            => new(version, checkpoint.Nodes.ToArray());
    }
}
