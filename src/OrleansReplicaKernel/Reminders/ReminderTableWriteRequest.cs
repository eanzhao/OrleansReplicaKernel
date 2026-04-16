namespace OrleansReplicaKernel.Reminders;

public sealed record ReminderTableWriteRequest(long ExpectedVersion, ReminderTableCheckpoint Checkpoint);
