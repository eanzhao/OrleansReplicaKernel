using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Reminders;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Demo;

public sealed partial class ReminderCounterGrain : IReminderCounterGrain, IRemindable
{
    private int _count;

    public async Task RegisterReminderAsync(
        string reminderName,
        int dueTimeMs,
        int periodMs,
        CancellationToken cancellationToken = default)
    {
        var reminderRegistry = ActivationExecutionContext.CurrentReminderRegistry
            ?? throw new InvalidOperationException("No reminder registry is available for the current activation.");

        await reminderRegistry.RegisterOrUpdateReminderAsync(
            reminderName,
            TimeSpan.FromMilliseconds(dueTimeMs),
            TimeSpan.FromMilliseconds(periodMs),
            cancellationToken);
        TraceLog.Write(
            "grain",
            $"ReminderCounterGrain register reminder={reminderName} due={dueTimeMs}ms period={periodMs}ms");
    }

    public async Task<bool> UnregisterReminderAsync(
        string reminderName,
        CancellationToken cancellationToken = default)
    {
        var reminderRegistry = ActivationExecutionContext.CurrentReminderRegistry
            ?? throw new InvalidOperationException("No reminder registry is available for the current activation.");

        return await reminderRegistry.UnregisterReminderAsync(reminderName, cancellationToken);
    }

    public async Task<string> GetRemindersSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var reminderRegistry = ActivationExecutionContext.CurrentReminderRegistry
            ?? throw new InvalidOperationException("No reminder registry is available for the current activation.");
        var reminders = await reminderRegistry.GetRemindersAsync(cancellationToken);
        return reminders.Count == 0
            ? "<none>"
            : string.Join(", ", reminders.Select(item => item.ReminderName));
    }

    public Task<int> GetCountAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_count);

    public Task ReceiveReminderAsync(
        string reminderName,
        ReminderTickStatus status,
        CancellationToken cancellationToken = default)
    {
        _count++;
        TraceLog.Write(
            "reminder",
            $"ReminderCounterGrain receive reminder={reminderName} count={_count} current={status.CurrentTickUtc:O}");
        return Task.CompletedTask;
    }
}
