namespace OrleansReplicaKernel.Reminders;

public interface IGrainReminderRegistry
{
    ValueTask<ReminderRegistration> RegisterOrUpdateReminderAsync(
        string reminderName,
        TimeSpan dueTime,
        TimeSpan period,
        CancellationToken cancellationToken = default);

    ValueTask<bool> UnregisterReminderAsync(
        string reminderName,
        CancellationToken cancellationToken = default);

    ValueTask<ReminderRegistration?> GetReminderAsync(
        string reminderName,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ReminderRegistration>> GetRemindersAsync(
        CancellationToken cancellationToken = default);
}
