namespace OrleansReplicaKernel.Reminders;

public readonly record struct ReminderTickStatus(
    DateTimeOffset FirstTickUtc,
    TimeSpan Period,
    DateTimeOffset CurrentTickUtc);
