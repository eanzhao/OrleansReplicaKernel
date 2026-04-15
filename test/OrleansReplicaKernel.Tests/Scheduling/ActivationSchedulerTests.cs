using OrleansReplicaKernel.Scheduling;
using OrleansReplicaKernel.Tests.TestSupport;

namespace OrleansReplicaKernel.Tests.Scheduling;

public sealed class ActivationSchedulerTests
{
    private static readonly Guid DefaultChainId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherChainId = Guid.Parse("22222222-2222-2222-2222-222222222222");

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
            requestChainId: DefaultChainId,
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
            requestChainId: DefaultChainId,
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
            requestChainId: DefaultChainId,
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
            requestChainId: DefaultChainId,
            async _ =>
            {
                exclusiveStarted.TrySetResult(true);
                await Task.Yield();
                return "second";
            },
            CancellationToken.None).AsTask();

        await AsyncTestSync.YieldUntilDispatchAsync();
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
            requestChainId: DefaultChainId,
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
            requestChainId: OtherChainId,
            async _ =>
            {
                interleavableStarted.TrySetResult(true);
                await Task.Yield();
                return "second";
            },
            CancellationToken.None).AsTask();

        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(interleavableStarted.Task.IsCompleted);

        exclusiveRelease.TrySetResult(true);

        await interleavableStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var results = await Task.WhenAll(first, second);
        Assert.Equal(["first", "second"], results);
    }

    [Fact]
    public async Task SameRequestChain_CanReenterActiveExclusiveTurn()
    {
        await using var scheduler = new ActivationScheduler("test/reentrant-exclusive");
        var outerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outerRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var innerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var outer = scheduler.EnqueueAsync(
            "outer",
            allowInterleaving: false,
            requestChainId: DefaultChainId,
            async _ =>
            {
                outerStarted.TrySetResult(true);
                await outerRelease.Task;
                return "outer";
            },
            CancellationToken.None).AsTask();

        await outerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var inner = scheduler.EnqueueAsync(
            "inner",
            allowInterleaving: false,
            requestChainId: DefaultChainId,
            async _ =>
            {
                innerStarted.TrySetResult(true);
                await Task.Yield();
                return "inner";
            },
            CancellationToken.None).AsTask();

        await innerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(outer.IsCompleted);

        outerRelease.TrySetResult(true);
        var results = await Task.WhenAll(outer, inner);
        Assert.Equal(["outer", "inner"], results);
    }

    [Fact]
    public async Task DifferentRequestChain_CannotReenterActiveExclusiveTurn()
    {
        await using var scheduler = new ActivationScheduler("test/non-reentrant-exclusive");
        var outerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outerRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var innerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var outer = scheduler.EnqueueAsync(
            "outer",
            allowInterleaving: false,
            requestChainId: DefaultChainId,
            async _ =>
            {
                outerStarted.TrySetResult(true);
                await outerRelease.Task;
                return "outer";
            },
            CancellationToken.None).AsTask();

        await outerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var inner = scheduler.EnqueueAsync(
            "inner",
            allowInterleaving: false,
            requestChainId: OtherChainId,
            async _ =>
            {
                innerStarted.TrySetResult(true);
                await Task.Yield();
                return "inner";
            },
            CancellationToken.None).AsTask();

        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(innerStarted.Task.IsCompleted);

        outerRelease.TrySetResult(true);
        await innerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var results = await Task.WhenAll(outer, inner);
        Assert.Equal(["outer", "inner"], results);
    }
}
