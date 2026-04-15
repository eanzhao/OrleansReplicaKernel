using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Demo;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Tests.TestSupport;
namespace OrleansReplicaKernel.Tests.Runtime;

public sealed class InvocationResponseSemanticsTests
{
    [Fact]
    public async Task CallerTimeout_DiscardsLateResponse_ButExecutionStillCompletes()
    {
        await using var host = CreateHost();
        var grain = host.GetGrain<IEchoGrain>("late-response");

        var seed = await grain.PingAsync("seed");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => grain.PingSlowAsync("slow", 150, timeout.Token));

        var dispositions = await host.WaitForAsync(
                probe: () => ValueTask.FromResult(host.GetResponseDispositionSnapshot("dev-node-1")),
                predicate: static snapshot => snapshot.LateResponses == 1,
                timeout: TimeSpan.FromSeconds(1),
                delayBetweenProbes: TimeSpan.FromMilliseconds(10))
            .WaitAsync(TimeSpan.FromSeconds(1));
        var afterTimeout = await grain.PingAsync("after-timeout");

        Assert.Equal("echo:seed:count=1", seed);
        Assert.Equal("echo:after-timeout:count=3", afterTimeout);
        Assert.Equal(1, dispositions.LateResponses);
        Assert.Equal(0, dispositions.StaleResponses);
        Assert.Equal(0, dispositions.DuplicateResponses);
    }

    [Fact]
    public async Task CallerTimeout_UsesConfiguredTimeProvider_AndStillDiscardsLateResponse()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        await using var host = CreateHost(timeProvider, TimeSpan.FromMinutes(5));
        var grain = host.GetGrain<IEchoGrain>("late-response-manual-time");

        var seed = await grain.PingAsync("seed");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50), host.TimeProvider);
        var slowCall = grain.PingSlowAsync("slow", 150, timeout.Token);

        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(slowCall.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(49));
        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(slowCall.IsCompleted);

        var timedOut = Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await slowCall);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await timedOut.WaitAsync(TimeSpan.FromSeconds(1));

        timeProvider.Advance(TimeSpan.FromMilliseconds(100));

        var dispositions = await host.WaitForAsync(
                probe: () => ValueTask.FromResult(host.GetResponseDispositionSnapshot("dev-node-1")),
                predicate: static snapshot => snapshot.LateResponses == 1,
                timeout: TimeSpan.FromSeconds(1))
            .WaitAsync(TimeSpan.FromSeconds(1));
        var afterTimeout = await grain.PingAsync("after-timeout");

        Assert.Equal("echo:seed:count=1", seed);
        Assert.Equal("echo:after-timeout:count=3", afterTimeout);
        Assert.Equal(1, dispositions.LateResponses);
        Assert.Equal(0, dispositions.StaleResponses);
        Assert.Equal(0, dispositions.DuplicateResponses);
    }

    [Fact]
    public async Task HostTimingHelpers_UseConfiguredTimeProvider()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        await using var host = CreateHost(timeProvider, TimeSpan.FromMinutes(5));

        var startedAt = host.GetTimestamp();
        var delay = host.DelayAsync(TimeSpan.FromMilliseconds(80));

        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(delay.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(79));
        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(delay.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await delay.WaitAsync(TimeSpan.FromSeconds(1));

        using var timeout = host.CreateTimeoutSource(TimeSpan.FromMilliseconds(50));
        Assert.False(timeout.IsCancellationRequested);

        timeProvider.Advance(TimeSpan.FromMilliseconds(49));
        Assert.False(timeout.IsCancellationRequested);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await AsyncTestSync.YieldUntilDispatchAsync();

        Assert.True(timeout.IsCancellationRequested);
        Assert.Equal(TimeSpan.FromMilliseconds(130), host.GetElapsedTime(startedAt));
    }

    [Fact]
    public async Task HostWaitForAsync_UsesConfiguredTimeProvider()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        await using var host = CreateHost(timeProvider, TimeSpan.FromMinutes(5));
        var gate = false;

        var waitTask = host.WaitForAsync(
            probe: () => ValueTask.FromResult(gate),
            predicate: static value => value,
            timeout: TimeSpan.FromMilliseconds(80));

        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(waitTask.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(79));
        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(waitTask.IsCompleted);

        gate = true;
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        await waitTask.WaitAsync(TimeSpan.FromSeconds(1));

        gate = false;
        var timedOutWait = host.WaitForAsync(
            probe: () => ValueTask.FromResult(gate),
            predicate: static value => value,
            timeout: TimeSpan.FromMilliseconds(50));

        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(timedOutWait.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(49));
        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(timedOutWait.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<TimeoutException>(async () => await timedOutWait.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task HostWaitForAsync_WithProbeDelay_UsesConfiguredTimeProvider()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        await using var host = CreateHost(timeProvider, TimeSpan.FromMinutes(5));
        var attempts = 0;

        var waitTask = host.WaitForAsync(
            probe: () => ValueTask.FromResult(++attempts >= 3),
            predicate: static value => value,
            timeout: TimeSpan.FromMilliseconds(80),
            delayBetweenProbes: TimeSpan.FromMilliseconds(20));

        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(waitTask.IsCompleted);
        Assert.Equal(1, attempts);

        timeProvider.Advance(TimeSpan.FromMilliseconds(19));
        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(waitTask.IsCompleted);
        Assert.Equal(1, attempts);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(waitTask.IsCompleted);
        Assert.Equal(2, attempts);

        timeProvider.Advance(TimeSpan.FromMilliseconds(20));
        await waitTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task DroppedResponse_RetriesSameRequestId_WithoutReexecutingGrainMethod()
    {
        await using var host = CreateHost("dev-node-1", "dev-node-2");
        var grain = host.GetGrain<IEchoGrain>("dedup");

        await host.SetOwnerAsync<IEchoGrain>("dedup", "dev-node-2");

        var seed = await grain.PingAsync("seed");

        host.DropNextResponse("dev-node-2", "test drop after execution");
        var afterDrop = await grain.PingAsync("after-drop");

        Assert.Equal("echo:seed:count=1", seed);
        Assert.Equal("echo:after-drop:count=2", afterDrop);
    }

    [Fact]
    public async Task DroppedResponse_ReplayedLater_IsDiscardedAsStaleAfterRetryCompletes()
    {
        await using var host = CreateHost("dev-node-1", "dev-node-2");
        var grain = host.GetGrain<IEchoGrain>("stale-response");

        await host.SetOwnerAsync<IEchoGrain>("stale-response", "dev-node-2");

        var seed = await grain.PingAsync("seed");

        host.DropNextResponseAndReplayLater(
            "dev-node-2",
            TimeSpan.FromMilliseconds(120),
            "test drop and replay later");
        var afterReplay = await grain.PingAsync("after-replay");
        var dispositions = await host.WaitForAsync(
                probe: () => ValueTask.FromResult(host.GetResponseDispositionSnapshot("dev-node-1")),
                predicate: static snapshot => snapshot.StaleResponses == 1,
                timeout: TimeSpan.FromSeconds(1),
                delayBetweenProbes: TimeSpan.FromMilliseconds(10))
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("echo:seed:count=1", seed);
        Assert.Equal("echo:after-replay:count=2", afterReplay);
        Assert.Equal(1, dispositions.StaleResponses);
        Assert.Equal(0, dispositions.LateResponses);
        Assert.Equal(0, dispositions.DuplicateResponses);
    }

    [Fact]
    public async Task DuplicatedResponse_AfterCompletion_IsDiscardedAsDuplicate()
    {
        await using var host = CreateHost("dev-node-1", "dev-node-2");
        var grain = host.GetGrain<IEchoGrain>("duplicate-response");

        await host.SetOwnerAsync<IEchoGrain>("duplicate-response", "dev-node-2");

        var seed = await grain.PingAsync("seed");

        host.DuplicateNextResponse("dev-node-2", TimeSpan.FromMilliseconds(100));
        var afterDuplicate = await grain.PingAsync("after-duplicate");
        var dispositions = await host.WaitForAsync(
                probe: () => ValueTask.FromResult(host.GetResponseDispositionSnapshot("dev-node-1")),
                predicate: static snapshot => snapshot.DuplicateResponses == 1,
                timeout: TimeSpan.FromSeconds(1),
                delayBetweenProbes: TimeSpan.FromMilliseconds(10))
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("echo:seed:count=1", seed);
        Assert.Equal("echo:after-duplicate:count=2", afterDuplicate);
        Assert.Equal(1, dispositions.DuplicateResponses);
        Assert.Equal(0, dispositions.StaleResponses);
    }

    [Fact]
    public async Task ResponseTrackingState_ExpiresAfterRetentionWindow()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 08, 0, 0, 0, TimeSpan.Zero));
        await using var host = CreateHost(
            timeProvider,
            TimeSpan.FromSeconds(5),
            "dev-node-1",
            "dev-node-2");
        var grain = host.GetGrain<IEchoGrain>("retention");

        await host.SetOwnerAsync<IEchoGrain>("retention", "dev-node-2");
        await grain.PingAsync("seed");

        var beforeExpirySource = host.GetResponseDispositionSnapshot("dev-node-1");
        var beforeExpiryTarget = host.GetResponseDispositionSnapshot("dev-node-2");

        timeProvider.Advance(TimeSpan.FromSeconds(6));

        var afterExpirySource = host.GetResponseDispositionSnapshot("dev-node-1");
        var afterExpiryTarget = host.GetResponseDispositionSnapshot("dev-node-2");

        Assert.Equal(1, beforeExpirySource.TrackedSourceRequests);
        Assert.Equal(1, beforeExpiryTarget.CompletedTargetRequests);
        Assert.Equal(0, afterExpirySource.TrackedSourceRequests);
        Assert.Equal(0, afterExpiryTarget.CompletedTargetRequests);
        Assert.Equal(0, afterExpirySource.PendingResponses);
        Assert.Equal(0, afterExpiryTarget.PendingResponses);
    }

    [Fact]
    public async Task TransportInjectedRequestDelay_UsesConfiguredTimeProvider()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        await using var host = CreateHost(
            timeProvider,
            TimeSpan.FromMinutes(5),
            "dev-node-1",
            "dev-node-2");
        var grain = host.GetGrain<IEchoGrain>("transport-delay");

        await host.SetOwnerAsync<IEchoGrain>("transport-delay", "dev-node-2");
        host.DelayNextRequest("dev-node-2", TimeSpan.FromMilliseconds(80));

        var delayedCall = grain.PingAsync("delayed");

        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(delayedCall.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(79));
        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(delayedCall.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        var result = await delayedCall.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("echo:delayed:count=1", result);
    }

    [Fact]
    public async Task ResponseDeliveryRetryDelay_UsesConfiguredTimeProvider()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        await using var host = CreateHost(
            timeProvider,
            TimeSpan.FromMinutes(5),
            "dev-node-1",
            "dev-node-2");
        var grain = host.GetGrain<IEchoGrain>("retry-delay");

        await host.SetOwnerAsync<IEchoGrain>("retry-delay", "dev-node-2");
        await grain.PingAsync("seed");

        host.DropNextResponse("dev-node-2", "drop and retry under manual time");
        var retriedCall = grain.PingAsync("after-drop");

        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(retriedCall.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(24));
        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(retriedCall.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        var result = await retriedCall.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("echo:after-drop:count=2", result);
    }

    [Fact]
    public async Task RemoteGrain_CanInvokeRegisteredCallbackTarget_OnSourceNode()
    {
        await using var host = CreateHost("dev-node-1", "dev-node-2");
        var grain = host.GetGrain<IEchoGrain>("callback");
        var observer = new RecordingEchoObserver();
        await using var callbackLease = host.CreateObjectReference<IEchoObserver>(
            callbackType: "echo-observer",
            implementation: observer);

        await host.SetOwnerAsync<IEchoGrain>("callback", "dev-node-2");

        var result = await grain.PingWithObserverAsync("notify", callbackLease.Handle);

        Assert.Equal("echo:notify:count=1", result);
        Assert.Equal(["observer:notify:count=1"], observer.Snapshot());
    }

    [Fact]
    public async Task UnregisteredCallbackTarget_CausesRemoteCallbackInvocationToFail()
    {
        await using var host = CreateHost("dev-node-1", "dev-node-2");
        var grain = host.GetGrain<IEchoGrain>("callback-disposed");
        var observer = new RecordingEchoObserver();
        var callbackLease = host.CreateObjectReference<IEchoObserver>(
            callbackType: "echo-observer",
            implementation: observer);
        var staleHandle = callbackLease.Handle;

        await host.SetOwnerAsync<IEchoGrain>("callback-disposed", "dev-node-2");
        await callbackLease.DisposeAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.PingWithObserverAsync("notify-after-dispose", staleHandle));

        Assert.Contains("No callback target registered", exception.Message);
    }

    [Fact]
    public async Task RemoteObserverCallback_CanReenterOriginatingGrainViaSameRequestChain()
    {
        await using var host = CreateHost("dev-node-1", "dev-node-2");
        var grain = host.GetGrain<IEchoGrain>("callback-reentrant");
        var observer = new ReentrantCallbackEchoObserver(grain);
        await using var callbackLease = host.CreateObjectReference<IEchoObserver>(
            callbackType: "echo-observer",
            implementation: observer);

        await host.SetOwnerAsync<IEchoGrain>("callback-reentrant", "dev-node-2");

        var result = await grain.PingWithObserverAsync("callback-reentrant", callbackLease.Handle)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("echo:callback-reentrant:count=2", result);
        Assert.Equal(["observer:callback-reentrant:count=1"], observer.Snapshot());
        Assert.Equal(["echo:callback-reenter:count=2"], observer.NestedResults());
    }

    [Fact]
    public async Task ActivationOwnedTimer_DoesNotBypassActiveExclusiveTurn()
    {
        await using var host = CreateHost();
        var grain = host.GetGrain<IEchoGrain>("timer-hold");

        var holdResult = await grain.HoldTurnWithTimerAsync("during-hold", holdDelayMs: 150, timerDelayMs: 30);
        var timerSnapshot = await host.WaitForAsync(
                probe: () => new ValueTask<string>(grain.GetTimerSnapshotAsync()),
                predicate: static snapshot => snapshot == "timer:during-hold:count=2",
                timeout: TimeSpan.FromSeconds(1),
                delayBetweenProbes: TimeSpan.FromMilliseconds(10))
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("hold:during-hold:count=1", holdResult);
        Assert.Equal("timer:during-hold:count=2", timerSnapshot);
    }

    [Fact]
    public async Task GrainSlowTurn_UsesConfiguredTimeProvider()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        await using var host = CreateHost(timeProvider, TimeSpan.FromMinutes(5));
        var grain = host.GetGrain<IEchoGrain>("slow-manual-time");

        var slowCall = grain.PingSlowAsync("manual-slow", 80);

        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(slowCall.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(79));
        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(slowCall.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        var result = await slowCall.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("echo:manual-slow:count=1", result);
    }

    [Fact]
    public async Task HoldTurnDelay_UsesConfiguredTimeProvider_WithoutLettingTimerBypassTurn()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        await using var host = CreateHost(timeProvider, TimeSpan.FromMinutes(5));
        var grain = host.GetGrain<IEchoGrain>("hold-manual-time");

        var holdCall = grain.HoldTurnWithTimerAsync("manual-hold", holdDelayMs: 80, timerDelayMs: 20);

        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(holdCall.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(20));
        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(holdCall.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(59));
        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(holdCall.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        var holdResult = await holdCall.WaitAsync(TimeSpan.FromSeconds(1));
        var timerSnapshot = await host.WaitForAsync(
                probe: () => new ValueTask<string>(grain.GetTimerSnapshotAsync()),
                predicate: static snapshot => snapshot == "timer:manual-hold:count=2",
                timeout: TimeSpan.FromSeconds(1))
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("hold:manual-hold:count=1", holdResult);
        Assert.Equal("timer:manual-hold:count=2", timerSnapshot);
    }

    [Fact]
    public async Task ActivationOwnedTimer_UsesConfiguredTimeProvider()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 15, 0, 0, 0, TimeSpan.Zero));
        await using var host = CreateHost(timeProvider, TimeSpan.FromMinutes(5));
        var grain = host.GetGrain<IEchoGrain>("timer-manual-time");

        await grain.ArmOneShotTimerAsync("manual", 80);

        Assert.Equal("<none>", await grain.GetTimerSnapshotAsync());

        timeProvider.Advance(TimeSpan.FromMilliseconds(79));
        Assert.Equal("<none>", await grain.GetTimerSnapshotAsync());

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        var snapshot = await host.WaitForAsync(
                probe: () => new ValueTask<string>(grain.GetTimerSnapshotAsync()),
                predicate: static current => current == "timer:manual:count=1",
                timeout: TimeSpan.FromSeconds(1))
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("timer:manual:count=1", snapshot);
    }

    [Fact]
    public async Task DeactivatingActivation_CancelsOwnedTimers()
    {
        await using var host = CreateHost();
        var grain = host.GetGrain<IEchoGrain>("timer-deactivate");

        await grain.ArmOneShotTimerAsync("cancelled", 80);
        await host.DeactivateGrainAsync<IEchoGrain>("timer-deactivate");
        await host.DelayAsync(TimeSpan.FromMilliseconds(140));

        var afterDeactivate = await host.GetGrain<IEchoGrain>("timer-deactivate").GetTimerSnapshotAsync();

        Assert.Equal("<none>", afterDeactivate);
    }

    [Fact]
    public async Task RawObserverImplementation_CannotCrossInvocationBoundary_WithoutReferenceSerialization()
    {
        await using var host = CreateHost("dev-node-1", "dev-node-2");
        var grain = host.GetGrain<IEchoGrain>("raw-observer");
        var rawObserver = new RecordingEchoObserver();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.PingWithObserverAsync("raw-observer", rawObserver));

        Assert.Contains("is not a runtime object reference", exception.Message);
    }

    [Fact]
    public async Task GeneratedGrainReferenceMetadata_BindsContractWithoutManualReferenceFactory()
    {
        await using var host = CreateHost();
        var counter = host.GetGrain<ICounterGrain>("generated-binding");

        var result = await counter.AddAsync(3);

        Assert.Equal(3, result);
    }

    [Fact]
    public async Task GeneratedGrainImplementationMetadata_ActivatesGrainWithoutManualImplementationFactory()
    {
        await using var host = CreateHost();
        var echo = host.GetGrain<IEchoGrain>("generated-activator");

        var result = await echo.PingAsync("generated-implementation");

        Assert.Equal("echo:generated-implementation:count=1", result);
    }

    [Fact]
    public async Task GeneratedGrainImplementationMetadata_CanOverrideIdleCollectionAgePerGrainType()
    {
        await using var host = CreateHost();
        var echo = host.GetGrain<IEchoGrain>("collection-echo");
        var counter = host.GetGrain<ICounterGrain>("collection-counter");

        await echo.PingAsync("seed");
        await counter.AddAsync(3);

        await host.DelayAsync(TimeSpan.FromMilliseconds(150));
        var collected = await host.CollectIdleGrainsAsync(TimeSpan.FromMilliseconds(300));

        var echoAfterCollect = await echo.PingAsync("after-collect");
        var counterAfterCollect = await counter.AddAsync(2);

        Assert.Equal(1, collected);
        Assert.Equal("echo:after-collect:count=2", echoAfterCollect);
        Assert.Equal(2, counterAfterCollect);
    }

    [Fact]
    public async Task GeneratedGrainImplementationMetadata_CanInfluenceInitialPlacement()
    {
        await using var host = CreateHost("dev-node-1", "dev-node-2", "dev-node-3");

        var echoA = host.GetGrain<IEchoGrain>("placement-load-a");
        var echoB = host.GetGrain<IEchoGrain>("placement-load-b");
        var counter = host.GetGrain<ICounterGrain>("prefer-local-placement");

        await echoA.PingAsync("seed-a");
        await echoB.PingAsync("seed-b");
        await counter.AddAsync(1);

        var directoryState = host.DescribeGrainDirectory();

        Assert.Contains("echo/placement-load-a->dev-node-1@v1", directoryState);
        Assert.Contains("echo/placement-load-b->dev-node-2@v1", directoryState);
        Assert.Contains("counter/prefer-local-placement->dev-node-1@v1", directoryState);
    }

    [Fact]
    public async Task GeneratedGrainImplementationMetadata_CanAllowInterleavingForSelectedMethods()
    {
        await using var host = CreateHost();
        var echo = host.GetGrain<IEchoGrain>("interleaving");
        var startedAt = host.GetTimestamp();

        var first = echo.PingSlowAsync("interleave-a", 150);
        await host.DelayAsync(TimeSpan.FromMilliseconds(20));
        var second = echo.PingSlowAsync("interleave-b", 150);

        var results = await Task.WhenAll(first, second);
        var elapsed = host.GetElapsedTime(startedAt);

        Assert.True(elapsed < TimeSpan.FromMilliseconds(260));
        Assert.Contains("echo:interleave-a:count=1", results);
        Assert.Contains("echo:interleave-b:count=2", results);
    }

    [Fact]
    public async Task SameCallChain_CanReenterExclusiveActivation()
    {
        await using var host = CreateHost();
        var echo = host.GetGrain<IEchoGrain>("reentrant-self");

        var result = await echo.ReentrantSelfCallAsync(2);

        Assert.Equal(3, result);
    }

    [Fact]
    public async Task NonInterleavableMethod_StillWaitsBehindInterleavableTurn()
    {
        await using var host = CreateHost();
        var echo = host.GetGrain<IEchoGrain>("interleaving-barrier");

        var first = echo.PingSlowAsync("slow-first", 150);
        await host.DelayAsync(TimeSpan.FromMilliseconds(20));
        var second = echo.PingAsync("exclusive-after-slow");

        await AsyncTestSync.YieldUntilDispatchAsync();
        Assert.False(second.IsCompleted);

        var results = await Task.WhenAll(first, second);
        Assert.Contains("echo:slow-first:count=1", results);
        Assert.Contains("echo:exclusive-after-slow:count=2", results);
    }

    private static OrleansReplicaKernelHost CreateHost(params string[] peerNodeNames)
        => CreateHost(TimeProvider.System, TimeSpan.FromMinutes(5), peerNodeNames);

    private static OrleansReplicaKernelHost CreateHost(
        TimeProvider timeProvider,
        TimeSpan responseHistoryRetention,
        params string[] peerNodeNames)
        => new OrleansReplicaKernelBuilder()
            .AddGeneratedGrainImplementationsFromAssembly(typeof(EchoGrain).Assembly)
            .WithTimeProvider(timeProvider)
            .WithResponseHistoryRetention(responseHistoryRetention)
            .AddGeneratedGrainReferencesFromAssembly(typeof(EchoGrainReference).Assembly)
            .AddGeneratedObjectReferencesFromAssembly(typeof(EchoObserverReference).Assembly)
            .Build("dev-node-1", peerNodeNames);
}
