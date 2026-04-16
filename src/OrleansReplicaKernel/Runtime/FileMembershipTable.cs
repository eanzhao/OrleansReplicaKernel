using System.Text.Json;

namespace OrleansReplicaKernel.Runtime;

public sealed class FileMembershipTable : IMembershipTable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly string _lockPath;

    public FileMembershipTable(string path)
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

    public async ValueTask<MembershipTableSnapshot> ReadAsync(CancellationToken cancellationToken = default)
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

    public ValueTask<bool> RegisterAsync(
        MembershipTableWriteRequest request,
        CancellationToken cancellationToken = default)
        => WriteAsync(request, cancellationToken);

    public ValueTask<bool> UpdateAsync(
        MembershipTableWriteRequest request,
        CancellationToken cancellationToken = default)
        => WriteAsync(request, cancellationToken);

    public ValueTask CleanupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<bool> WriteAsync(
        MembershipTableWriteRequest request,
        CancellationToken cancellationToken)
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
                    var document = MembershipTableFileDocument.From(
                        MembershipTableCheckpointHelper.Clone(request.Checkpoint),
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

    private async ValueTask<MembershipTableSnapshot> ReadSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new MembershipTableSnapshot(0, ClusterMembershipCheckpoint.Empty);
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
            return new MembershipTableSnapshot(0, ClusterMembershipCheckpoint.Empty);
        }

        var document = await JsonSerializer.DeserializeAsync<MembershipTableFileDocument>(
            stream,
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException($"Membership table '{_path}' contains invalid JSON.");

        return new MembershipTableSnapshot(
            document.Version,
            new ClusterMembershipCheckpoint(
                document.CurrentEpoch,
                document.Members
                    .OrderBy(item => item.NodeName, StringComparer.Ordinal)
                    .ToArray(),
                document.ViewChanges
                    .OrderBy(item => item.Epoch)
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

    private sealed record MembershipTableFileDocument(
        long Version,
        long CurrentEpoch,
        ClusterMemberRecord[] Members,
        MembershipViewChange[] ViewChanges)
    {
        public static MembershipTableFileDocument From(ClusterMembershipCheckpoint checkpoint, long version)
            => new(
                version,
                checkpoint.CurrentEpoch,
                checkpoint.Members.ToArray(),
                checkpoint.ViewChanges.ToArray());
    }
}
