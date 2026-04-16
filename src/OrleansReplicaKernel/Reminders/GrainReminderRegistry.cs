using OrleansReplicaKernel.Identity;

namespace OrleansReplicaKernel.Reminders;

internal sealed class GrainReminderRegistry : IGrainReminderRegistry
{
    private readonly GrainId _grainId;
    private readonly LocalReminderService _service;

    public GrainReminderRegistry(GrainId grainId, LocalReminderService service)
    {
        _grainId = grainId;
        _service = service;
    }

    public ValueTask<ReminderRegistration> RegisterOrUpdateReminderAsync(
        string reminderName,
        TimeSpan dueTime,
        TimeSpan period,
        CancellationToken cancellationToken = default)
        => _service.RegisterOrUpdateReminderAsync(_grainId, reminderName, dueTime, period, cancellationToken);

    public ValueTask<bool> UnregisterReminderAsync(
        string reminderName,
        CancellationToken cancellationToken = default)
        => _service.UnregisterReminderAsync(_grainId, reminderName, cancellationToken);

    public ValueTask<ReminderRegistration?> GetReminderAsync(
        string reminderName,
        CancellationToken cancellationToken = default)
        => _service.GetReminderAsync(_grainId, reminderName, cancellationToken);

    public ValueTask<IReadOnlyList<ReminderRegistration>> GetRemindersAsync(
        CancellationToken cancellationToken = default)
        => _service.GetRemindersAsync(_grainId, cancellationToken);
}
