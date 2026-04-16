using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Reminders;

namespace OrleansReplicaKernel.Tests.Runtime;

public sealed class FileReminderTableTests
{
    [Fact]
    public async Task WriteAsync_PersistsReminderRegistrations()
    {
        var path = CreateReminderTablePath();

        try
        {
            var table = new FileReminderTable(path);
            var initial = await table.ReadAsync();
            var reminder = new ReminderRegistration(
                new GrainId("reminderCounter", "alpha"),
                "tick",
                new DateTimeOffset(2026, 04, 16, 0, 0, 1, TimeSpan.Zero),
                new DateTimeOffset(2026, 04, 16, 0, 0, 1, TimeSpan.Zero),
                TimeSpan.FromSeconds(5));

            var written = await table.WriteAsync(
                new ReminderTableWriteRequest(
                    initial.Version,
                    new ReminderTableCheckpoint([reminder])));

            var snapshot = await table.ReadAsync();

            Assert.True(written);
            Assert.Equal(1, snapshot.Version);
            Assert.Equal([reminder], snapshot.Checkpoint.Reminders);
        }
        finally
        {
            DeleteReminderTableArtifacts(path);
        }
    }

    [Fact]
    public async Task WriteAsync_RejectsVersionConflicts()
    {
        var path = CreateReminderTablePath();

        try
        {
            var table = new FileReminderTable(path);
            var initial = await table.ReadAsync();
            var reminder = new ReminderRegistration(
                new GrainId("reminderCounter", "alpha"),
                "tick",
                new DateTimeOffset(2026, 04, 16, 0, 0, 1, TimeSpan.Zero),
                new DateTimeOffset(2026, 04, 16, 0, 0, 1, TimeSpan.Zero),
                TimeSpan.FromSeconds(5));

            Assert.True(await table.WriteAsync(
                new ReminderTableWriteRequest(
                    initial.Version,
                    new ReminderTableCheckpoint([reminder]))));

            var conflicted = await table.WriteAsync(
                new ReminderTableWriteRequest(
                    initial.Version,
                    ReminderTableCheckpoint.Empty));

            Assert.False(conflicted);
        }
        finally
        {
            DeleteReminderTableArtifacts(path);
        }
    }

    private static string CreateReminderTablePath()
        => Path.Combine(
            Path.GetTempPath(),
            "OrleansReplicaKernel.Tests",
            "reminder-table",
            Guid.NewGuid().ToString("N") + ".json");

    private static void DeleteReminderTableArtifacts(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var lockPath = path + ".lock";
        if (File.Exists(lockPath))
        {
            File.Delete(lockPath);
        }
    }
}
