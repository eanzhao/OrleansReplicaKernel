using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Reminders;

public sealed record ReminderRegistration(
    GrainId GrainId,
    string ReminderName,
    DateTimeOffset FirstTickUtc,
    DateTimeOffset NextTickUtc,
    TimeSpan Period);
