namespace OrleansReplicaKernel.Reminders;

public sealed record ReminderTableCheckpoint(IReadOnlyList<ReminderRegistration> Reminders)
{
    public static ReminderTableCheckpoint Empty { get; } = new(Array.Empty<ReminderRegistration>());
}
