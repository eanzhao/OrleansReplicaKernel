using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Demo;
using OrleansReplicaKernel.Tests.TestSupport;

namespace OrleansReplicaKernel.Tests.Runtime;

public sealed class ReminderIntegrationTests
{
    [Fact]
    public async Task Reminder_FiresAfterDeactivate_AndReactivatesGrain()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        var reminderTablePath = CreateReminderTablePath();

        try
        {
            await using var host = CreateHost(timeProvider, reminderTablePath);
            var grain = host.GetGrain<IReminderCounterGrain>("reactivate");

            await grain.RegisterReminderAsync("tick", dueTimeMs: 100, periodMs: 5_000);
            Assert.Equal("tick", await grain.GetRemindersSnapshotAsync());

            await host.DeactivateGrainAsync<IReminderCounterGrain>("reactivate");

            timeProvider.Advance(TimeSpan.FromMilliseconds(150));

            var count = await WaitForCountAsync(host, "reactivate", expected: 1);

            Assert.Equal(1, count);

            var removed = await host.GetGrain<IReminderCounterGrain>("reactivate").UnregisterReminderAsync("tick");
            Assert.True(removed);
            Assert.Equal("<none>", await host.GetGrain<IReminderCounterGrain>("reactivate").GetRemindersSnapshotAsync());
        }
        finally
        {
            DeleteReminderTableArtifacts(reminderTablePath);
        }
    }

    [Fact]
    public async Task Reminder_ContinuesFiringAfterOwnerMovesToAnotherNode()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        var reminderTablePath = CreateReminderTablePath();

        try
        {
            await using var host = CreateHost(timeProvider, reminderTablePath, "dev-node-2");
            var grain = host.GetGrain<IReminderCounterGrain>("move");

            await grain.RegisterReminderAsync("migrate", dueTimeMs: 100, periodMs: 5_000);
            await host.DeactivateGrainAsync<IReminderCounterGrain>("move");
            await host.SetOwnerAsync<IReminderCounterGrain>("move", "dev-node-2");

            timeProvider.Advance(TimeSpan.FromMilliseconds(150));

            var count = await WaitForCountAsync(host, "move", expected: 1);

            Assert.Equal(1, count);
            Assert.Equal("migrate", await host.GetGrain<IReminderCounterGrain>("move").GetRemindersSnapshotAsync());
        }
        finally
        {
            DeleteReminderTableArtifacts(reminderTablePath);
        }
    }

    private static OrleansReplicaKernelHost CreateHost(
        TimeProvider timeProvider,
        string reminderTablePath,
        params string[] peerNodeNames)
        => new OrleansReplicaKernelBuilder()
            .AddGeneratedGrainImplementationsFromAssembly(typeof(EchoGrain).Assembly)
            .AddGeneratedGrainReferencesFromAssembly(typeof(EchoGrainReference).Assembly)
            .AddGeneratedObjectReferencesFromAssembly(typeof(EchoObserverReference).Assembly)
            .WithTimeProvider(timeProvider)
            .UseFileReminderTable(reminderTablePath)
            .WithReminderScanInterval(TimeSpan.FromMilliseconds(20))
            .Build("dev-node-1", peerNodeNames);

    private static async Task<int> WaitForCountAsync(
        OrleansReplicaKernelHost host,
        string key,
        int expected)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var count = await host.GetGrain<IReminderCounterGrain>(key).GetCountAsync();
            if (count == expected)
            {
                return count;
            }

            await AsyncTestSync.YieldUntilDispatchAsync(iterations: 4);
        }

        return await host.GetGrain<IReminderCounterGrain>(key).GetCountAsync();
    }

    private static string CreateReminderTablePath()
        => Path.Combine(
            Path.GetTempPath(),
            "OrleansReplicaKernel.Tests",
            "reminders",
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
