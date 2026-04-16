namespace OrleansReplicaKernel.Demo;

public interface IReminderCounterGrain
{
    Task RegisterReminderAsync(
        string reminderName,
        int dueTimeMs,
        int periodMs,
        CancellationToken cancellationToken = default);

    Task<bool> UnregisterReminderAsync(
        string reminderName,
        CancellationToken cancellationToken = default);

    Task<string> GetRemindersSnapshotAsync(CancellationToken cancellationToken = default);

    Task<int> GetCountAsync(CancellationToken cancellationToken = default);
}
