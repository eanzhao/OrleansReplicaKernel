namespace OrleansReplicaKernel.Reminders;

public interface IReminderTable
{
    ValueTask<ReminderTableSnapshot> ReadAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> WriteAsync(
        ReminderTableWriteRequest request,
        CancellationToken cancellationToken = default);
}
