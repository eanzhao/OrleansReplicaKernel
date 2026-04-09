using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Demo;
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

        await Task.Delay(TimeSpan.FromMilliseconds(200));

        var afterTimeout = await grain.PingAsync("after-timeout");
        var dispositions = host.GetResponseDispositionSnapshot("dev-node-1");

        Assert.Equal("echo:seed:count=1", seed);
        Assert.Equal("echo:after-timeout:count=3", afterTimeout);
        Assert.Equal(1, dispositions.LateResponses);
        Assert.Equal(0, dispositions.StaleResponses);
        Assert.Equal(0, dispositions.DuplicateResponses);
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
        await Task.Delay(TimeSpan.FromMilliseconds(160));

        var dispositions = host.GetResponseDispositionSnapshot("dev-node-1");

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
        await Task.Delay(TimeSpan.FromMilliseconds(140));

        var dispositions = host.GetResponseDispositionSnapshot("dev-node-1");

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

        await Task.Delay(TimeSpan.FromMilliseconds(150));
        var collected = await host.CollectIdleGrainsAsync(TimeSpan.FromMilliseconds(300));

        var echoAfterCollect = await echo.PingAsync("after-collect");
        var counterAfterCollect = await counter.AddAsync(2);

        Assert.Equal(1, collected);
        Assert.Equal("echo:after-collect:count=2", echoAfterCollect);
        Assert.Equal(2, counterAfterCollect);
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
