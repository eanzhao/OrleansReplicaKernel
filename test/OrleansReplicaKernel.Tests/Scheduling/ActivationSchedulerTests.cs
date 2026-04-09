using OrleansReplicaKernel.Scheduling;

namespace OrleansReplicaKernel.Tests.Scheduling;

public sealed class ActivationSchedulerTests
{
    [Fact]
    public async Task InterleavableTurns_CanOverlap()
    {
        await using var scheduler = new ActivationScheduler("test/interleave");
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = scheduler.EnqueueAsync(
            "first",
            allowInterleaving: true,
            async _ =>
            {
                firstStarted.TrySetResult(true);
                await firstRelease.Task;
                return "first";
            },
            CancellationToken.None).AsTask();

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var second = scheduler.EnqueueAsync(
            "second",
            allowInterleaving: true,
            async _ =>
            {
                secondStarted.TrySetResult(true);
                await Task.Yield();
                return "second";
            },
            CancellationToken.None).AsTask();

        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(first.IsCompleted);

        firstRelease.TrySetResult(true);

        var results = await Task.WhenAll(first, second);
        Assert.Equal(["first", "second"], results);
    }

    [Fact]
    public async Task ExclusiveTurn_WaitsForEarlierInterleavableTurnToFinish()
    {
        await using var scheduler = new ActivationScheduler("test/exclusive-after-interleave");
        var interleavableStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interleavableRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exclusiveStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = scheduler.EnqueueAsync(
            "interleavable",
            allowInterleaving: true,
            async _ =>
            {
                interleavableStarted.TrySetResult(true);
                await interleavableRelease.Task;
                return "first";
            },
            CancellationToken.None).AsTask();

        await interleavableStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var second = scheduler.EnqueueAsync(
            "exclusive",
            allowInterleaving: false,
            async _ =>
            {
                exclusiveStarted.TrySetResult(true);
                await Task.Yield();
                return "second";
            },
            CancellationToken.None).AsTask();

        await Task.Delay(100);
        Assert.False(exclusiveStarted.Task.IsCompleted);

        interleavableRelease.TrySetResult(true);

        await exclusiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var results = await Task.WhenAll(first, second);
        Assert.Equal(["first", "second"], results);
    }

    [Fact]
    public async Task LaterInterleavableTurn_DoesNotBypassQueuedExclusiveTurn()
    {
        await using var scheduler = new ActivationScheduler("test/interleave-after-exclusive");
        var exclusiveStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exclusiveRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interleavableStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = scheduler.EnqueueAsync(
            "exclusive",
            allowInterleaving: false,
            async _ =>
            {
                exclusiveStarted.TrySetResult(true);
                await exclusiveRelease.Task;
                return "first";
            },
            CancellationToken.None).AsTask();

        await exclusiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var second = scheduler.EnqueueAsync(
            "interleavable",
            allowInterleaving: true,
            async _ =>
            {
                interleavableStarted.TrySetResult(true);
                await Task.Yield();
                return "second";
            },
            CancellationToken.None).AsTask();

        await Task.Delay(100);
        Assert.False(interleavableStarted.Task.IsCompleted);

        exclusiveRelease.TrySetResult(true);

        await interleavableStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var results = await Task.WhenAll(first, second);
        Assert.Equal(["first", "second"], results);
    }
}
