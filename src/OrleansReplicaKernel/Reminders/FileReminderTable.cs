using System.Text.Json;

namespace OrleansReplicaKernel.Reminders;

public sealed class FileReminderTable : IReminderTable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly string _lockPath;

    public FileReminderTable(string path)
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

    public async ValueTask<ReminderTableSnapshot> ReadAsync(CancellationToken cancellationToken = default)
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
        ReminderTableWriteRequest request,
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
                    var document = new ReminderTableFileDocument(
                        current.Version + 1,
                        request.Checkpoint.Reminders
                            .OrderBy(item => item.GrainId.ToString(), StringComparer.Ordinal)
                            .ThenBy(item => item.ReminderName, StringComparer.Ordinal)
                            .ToArray());

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

    private async ValueTask<ReminderTableSnapshot> ReadSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new ReminderTableSnapshot(0, ReminderTableCheckpoint.Empty);
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
            return new ReminderTableSnapshot(0, ReminderTableCheckpoint.Empty);
        }

        var document = await JsonSerializer.DeserializeAsync<ReminderTableFileDocument>(
            stream,
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException($"Reminder table '{_path}' contains invalid JSON.");

        return new ReminderTableSnapshot(
            document.Version,
            new ReminderTableCheckpoint(
                document.Reminders
                    .OrderBy(item => item.GrainId.ToString(), StringComparer.Ordinal)
                    .ThenBy(item => item.ReminderName, StringComparer.Ordinal)
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

    private sealed record ReminderTableFileDocument(long Version, ReminderRegistration[] Reminders);
}
