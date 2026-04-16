using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Routing;

namespace OrleansReplicaKernel.Reminders;

internal sealed class LocalReminderService : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly string _localNodeName;
    private readonly IReminderTable _reminderTable;
    private readonly IGrainDirectory _grainDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _scanInterval;
    private readonly HashSet<ReminderKey> _inflight = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private IInvocationRuntime? _runtime;

    public LocalReminderService(
        string localNodeName,
        IReminderTable reminderTable,
        IGrainDirectory grainDirectory,
        TimeProvider timeProvider,
        TimeSpan scanInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localNodeName);
        if (scanInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(scanInterval), "Reminder scan interval must be positive.");
        }

        _localNodeName = localNodeName;
        _reminderTable = reminderTable ?? throw new ArgumentNullException(nameof(reminderTable));
        _grainDirectory = grainDirectory ?? throw new ArgumentNullException(nameof(grainDirectory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _scanInterval = scanInterval;
        _loop = Task.Run(RunAsync);
    }

    public void Bind(IInvocationRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public IGrainReminderRegistry BindToGrain(GrainId grainId)
        => new GrainReminderRegistry(grainId, this);

    public async ValueTask<ReminderRegistration> RegisterOrUpdateReminderAsync(
        GrainId grainId,
        string reminderName,
        TimeSpan dueTime,
        TimeSpan period,
        CancellationToken cancellationToken = default)
    {
        ValidateRegistration(reminderName, dueTime, period);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = await _reminderTable.ReadAsync(cancellationToken);
            var now = _timeProvider.GetUtcNow();
            var reminder = new ReminderRegistration(
                grainId,
                reminderName,
                FirstTickUtc: now.Add(dueTime),
                NextTickUtc: now.Add(dueTime),
                period);
            var updatedCheckpoint = new ReminderTableCheckpoint(
                snapshot.Checkpoint.Reminders
                    .Where(item => item.GrainId != grainId
                        || !string.Equals(item.ReminderName, reminderName, StringComparison.Ordinal))
                    .Append(reminder)
                    .OrderBy(item => item.GrainId.ToString(), StringComparer.Ordinal)
                    .ThenBy(item => item.ReminderName, StringComparer.Ordinal)
                    .ToArray());

            if (await _reminderTable.WriteAsync(
                    new ReminderTableWriteRequest(snapshot.Version, updatedCheckpoint),
                    cancellationToken))
            {
                TraceLog.Write(
                    "reminder",
                    $"register {grainId} reminder={reminderName} due={dueTime} period={period} on {_localNodeName}");
                return reminder;
            }
        }
    }

    public async ValueTask<bool> UnregisterReminderAsync(
        GrainId grainId,
        string reminderName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderName);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = await _reminderTable.ReadAsync(cancellationToken);
            var existing = snapshot.Checkpoint.Reminders.Any(item =>
                item.GrainId == grainId
                && string.Equals(item.ReminderName, reminderName, StringComparison.Ordinal));
            if (!existing)
            {
                return false;
            }

            var updatedCheckpoint = new ReminderTableCheckpoint(
                snapshot.Checkpoint.Reminders
                    .Where(item => item.GrainId != grainId
                        || !string.Equals(item.ReminderName, reminderName, StringComparison.Ordinal))
                    .OrderBy(item => item.GrainId.ToString(), StringComparer.Ordinal)
                    .ThenBy(item => item.ReminderName, StringComparer.Ordinal)
                    .ToArray());

            if (await _reminderTable.WriteAsync(
                    new ReminderTableWriteRequest(snapshot.Version, updatedCheckpoint),
                    cancellationToken))
            {
                lock (_lock)
                {
                    _inflight.Remove(new ReminderKey(grainId, reminderName));
                }

                TraceLog.Write(
                    "reminder",
                    $"unregister {grainId} reminder={reminderName} on {_localNodeName}");
                return true;
            }
        }
    }

    public async ValueTask<ReminderRegistration?> GetReminderAsync(
        GrainId grainId,
        string reminderName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderName);

        var snapshot = await _reminderTable.ReadAsync(cancellationToken);
        return snapshot.Checkpoint.Reminders.FirstOrDefault(item =>
            item.GrainId == grainId
            && string.Equals(item.ReminderName, reminderName, StringComparison.Ordinal));
    }

    public async ValueTask<IReadOnlyList<ReminderRegistration>> GetRemindersAsync(
        GrainId grainId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _reminderTable.ReadAsync(cancellationToken);
        return snapshot.Checkpoint.Reminders
            .Where(item => item.GrainId == grainId)
            .OrderBy(item => item.ReminderName, StringComparer.Ordinal)
            .ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        try
        {
            await _loop;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await ScanAsync(_cts.Token);
                await Task.Delay(_scanInterval, _timeProvider, _cts.Token);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _reminderTable.ReadAsync(cancellationToken);
        var utcNow = _timeProvider.GetUtcNow();

        foreach (var reminder in snapshot.Checkpoint.Reminders
                     .Where(item => item.NextTickUtc <= utcNow)
                     .OrderBy(item => item.NextTickUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_grainDirectory.Resolve(reminder.GrainId).OwnerNodeName != _localNodeName)
            {
                continue;
            }

            var key = new ReminderKey(reminder.GrainId, reminder.ReminderName);
            lock (_lock)
            {
                if (!_inflight.Add(key))
                {
                    continue;
                }
            }

            _ = FireReminderAsync(reminder, cancellationToken);
        }
    }

    private async Task FireReminderAsync(ReminderRegistration reminder, CancellationToken cancellationToken)
    {
        var key = new ReminderKey(reminder.GrainId, reminder.ReminderName);
        try
        {
            var runtime = _runtime
                ?? throw new InvalidOperationException("Reminder service has not been bound to a runtime.");
            var tickStatus = new ReminderTickStatus(
                reminder.FirstTickUtc,
                reminder.Period,
                reminder.NextTickUtc);

            TraceLog.Write(
                "reminder",
                $"fire {reminder.GrainId} reminder={reminder.ReminderName} at={tickStatus.CurrentTickUtc:O} on {_localNodeName}");
            await runtime.InvokeAsync<object?>(
                reminder.GrainId,
                new ReminderTickInvokable(reminder.ReminderName, tickStatus),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            TraceLog.Write(
                "reminder",
                $"fire failed {reminder.GrainId} reminder={reminder.ReminderName} on {_localNodeName}: {exception.Message}");
        }
        finally
        {
            try
            {
                await AdvanceReminderAsync(reminder, cancellationToken);
            }
            finally
            {
                lock (_lock)
                {
                    _inflight.Remove(key);
                }
            }
        }
    }

    private async Task AdvanceReminderAsync(ReminderRegistration reminder, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = await _reminderTable.ReadAsync(cancellationToken);
            var current = snapshot.Checkpoint.Reminders.FirstOrDefault(item =>
                item.GrainId == reminder.GrainId
                && string.Equals(item.ReminderName, reminder.ReminderName, StringComparison.Ordinal));
            if (current is null)
            {
                return;
            }

            var utcNow = _timeProvider.GetUtcNow();
            var nextTickUtc = current.NextTickUtc;
            while (nextTickUtc <= utcNow)
            {
                nextTickUtc = nextTickUtc.Add(current.Period);
            }

            if (nextTickUtc == current.NextTickUtc)
            {
                return;
            }

            var updated = current with { NextTickUtc = nextTickUtc };
            var updatedCheckpoint = new ReminderTableCheckpoint(
                snapshot.Checkpoint.Reminders
                    .Where(item => item.GrainId != current.GrainId
                        || !string.Equals(item.ReminderName, current.ReminderName, StringComparison.Ordinal))
                    .Append(updated)
                    .OrderBy(item => item.GrainId.ToString(), StringComparer.Ordinal)
                    .ThenBy(item => item.ReminderName, StringComparer.Ordinal)
                    .ToArray());

            if (await _reminderTable.WriteAsync(
                    new ReminderTableWriteRequest(snapshot.Version, updatedCheckpoint),
                    cancellationToken))
            {
                return;
            }
        }
    }

    private static void ValidateRegistration(string reminderName, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderName);

        if (dueTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(dueTime), "Reminder due time must be non-negative.");
        }

        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period), "Reminder period must be positive.");
        }
    }

    private sealed record ReminderKey(GrainId GrainId, string ReminderName);

    private sealed class ReminderTickInvokable : IInvokable
    {
        private readonly string _reminderName;
        private readonly ReminderTickStatus _status;

        public ReminderTickInvokable(string reminderName, ReminderTickStatus status)
        {
            _reminderName = reminderName;
            _status = status;
        }

        public string InterfaceName => nameof(IRemindable);

        public string InterfaceCompatibilityFamily => nameof(IRemindable);

        public int InterfaceVersion => 1;

        public string MethodName => nameof(IRemindable.ReceiveReminderAsync);

        public async ValueTask<object?> InvokeAsync(object target, CancellationToken cancellationToken)
        {
            if (target is not IRemindable remindable)
            {
                throw new InvalidOperationException(
                    $"Reminder target '{target.GetType().FullName}' does not implement {nameof(IRemindable)}.");
            }

            await remindable.ReceiveReminderAsync(_reminderName, _status, cancellationToken);
            return null;
        }
    }
}
