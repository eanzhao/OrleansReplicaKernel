namespace OrleansReplicaKernel.Reminders;

public sealed record ReminderTableSnapshot(long Version, ReminderTableCheckpoint Checkpoint);
