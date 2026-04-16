namespace OrleansReplicaKernel.Reminders;

public interface IRemindable
{
    Task ReceiveReminderAsync(
        string reminderName,
        ReminderTickStatus status,
        CancellationToken cancellationToken = default);
}
