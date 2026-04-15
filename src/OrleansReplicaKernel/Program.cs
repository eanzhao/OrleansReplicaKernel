using System.Diagnostics;
using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Demo;
using OrleansReplicaKernel.Runtime;

var stabilizationWindow = TimeSpan.FromMilliseconds(200);
var gossipFanout = 1;
var antiEntropyInterval = 4;

OrleansReplicaKernelHost host = CreateHost();
try
{
    TraceLog.Write("app", "build host complete");
    TraceLog.Write("app", $"gossip settings fanout={gossipFanout} anti-entropy-every={antiEntropyInterval}");
    LogMembershipViews("membership-initial");

    var echo = host.GetGrain<IEchoGrain>("alpha");
    var counter = host.GetGrain<ICounterGrain>("beta");

    var firstEcho = await echo.PingAsync("hello");

    Console.WriteLine();

    var secondEcho = await echo.PingAsync("again");

    Console.WriteLine();
    TraceLog.Write("app", "move echo owner to remote node dev-node-2");
    await host.SetOwnerAsync<IEchoGrain>("alpha", "dev-node-2");

    var remoteEcho = await echo.PingAsync("remote-owner");

    Console.WriteLine();
    TraceLog.Write("result", $"echo-remote = {remoteEcho}");

    var fenceEcho = host.GetGrain<IEchoGrain>("fence");
    var fenceSeed = await fenceEcho.PingAsync("fence-seed");

    Console.WriteLine();
    TraceLog.Write("app", "move fence owner to remote node dev-node-2");
    await host.SetOwnerAsync<IEchoGrain>("fence", "dev-node-2");
    var fenceRemote = await fenceEcho.PingAsync("fence-remote-owner");

    Console.WriteLine();
    TraceLog.Write("app", "delay a request to dev-node-2, then move owner to dev-node-3 and force stale-message rejection + retry");
    host.DelayNextRequest("dev-node-2", TimeSpan.FromMilliseconds(150));
    var staleRetryTask = fenceEcho.PingAsync("after-stale-retry");
    await PauseAsync(TimeSpan.FromMilliseconds(30));
    await host.SetOwnerAsync<IEchoGrain>("fence", "dev-node-3");
    var fenceAfterStaleRetry = await staleRetryTask;

    Console.WriteLine();
    TraceLog.Write("result", $"echo-fence-seed = {fenceSeed}");
    TraceLog.Write("result", $"echo-fence-remote = {fenceRemote}");
    TraceLog.Write("result", $"echo-fence-after-stale-retry = {fenceAfterStaleRetry}");

    var dedupEcho = host.GetGrain<IEchoGrain>("dedup");
    var dedupSeed = await dedupEcho.PingAsync("dedup-seed");

    Console.WriteLine();
    TraceLog.Write("app", "move dedup owner to remote node dev-node-2");
    await host.SetOwnerAsync<IEchoGrain>("dedup", "dev-node-2");
    TraceLog.Write("app", "drop the next response from dev-node-2 after execution, then retry the same request id");
    host.DropNextResponse("dev-node-2", "simulated response drop after execution");
    var dedupAfterDroppedResponse = await dedupEcho.PingAsync("after-dropped-response");

    Console.WriteLine();
    TraceLog.Write("result", $"echo-dedup-seed = {dedupSeed}");
    TraceLog.Write("result", $"echo-dedup-after-dropped-response = {dedupAfterDroppedResponse}");

    var lateResponseEcho = host.GetGrain<IEchoGrain>("late-response");
    var lateResponseSeed = await lateResponseEcho.PingAsync("late-response-seed");

    Console.WriteLine();
    TraceLog.Write("app", "start a slow local request with a short caller timeout, then let the response arrive late and get discarded");
    using var lateResponseTimeout = CreateTimeout(TimeSpan.FromMilliseconds(50));
    string lateResponseTimeoutResult;
    try
    {
        await lateResponseEcho.PingSlowAsync("late-response-timeout", 150, lateResponseTimeout.Token);
        lateResponseTimeoutResult = "<unexpected-success>";
    }
    catch (OperationCanceledException exception)
    {
        lateResponseTimeoutResult = exception.GetType().Name;
    }

    await PauseAsync(TimeSpan.FromMilliseconds(200));
    var lateResponseAfterTimeout = await lateResponseEcho.PingAsync("late-response-after-timeout");

    Console.WriteLine();
    TraceLog.Write("result", $"echo-late-response-seed = {lateResponseSeed}");
    TraceLog.Write("result", $"echo-late-response-timeout = {lateResponseTimeoutResult}");
    TraceLog.Write("result", $"echo-late-response-after-timeout = {lateResponseAfterTimeout}");

    var responseOrderingEcho = host.GetGrain<IEchoGrain>("response-ordering");
    var responseOrderingSeed = await responseOrderingEcho.PingAsync("response-ordering-seed");

    Console.WriteLine();
    TraceLog.Write("app", "move response-ordering owner to remote node dev-node-2");
    await host.SetOwnerAsync<IEchoGrain>("response-ordering", "dev-node-2");
    TraceLog.Write("app", "drop the next response from dev-node-2, but replay it later so the retry wins and the old response becomes stale");
    host.DropNextResponseAndReplayLater(
        "dev-node-2",
        TimeSpan.FromMilliseconds(150),
        "simulated dropped response that leaks back later");
    var responseOrderingAfterStaleReplay = await responseOrderingEcho.PingAsync("after-stale-response");
    await PauseAsync(TimeSpan.FromMilliseconds(200));

    Console.WriteLine();
    TraceLog.Write("app", "duplicate the next successful response from dev-node-2 after the caller has already completed");
    host.DuplicateNextResponse("dev-node-2", TimeSpan.FromMilliseconds(120));
    var responseOrderingAfterDuplicate = await responseOrderingEcho.PingAsync("after-duplicate-response");
    await PauseAsync(TimeSpan.FromMilliseconds(160));

    Console.WriteLine();
    TraceLog.Write("result", $"echo-response-ordering-seed = {responseOrderingSeed}");
    TraceLog.Write("result", $"echo-response-ordering-after-stale = {responseOrderingAfterStaleReplay}");
    TraceLog.Write("result", $"echo-response-ordering-after-duplicate = {responseOrderingAfterDuplicate}");
    TraceLog.Write("result", $"response-disposition-before-runtime-checkpoint[dev-node-1] = {host.DescribeResponseDisposition("dev-node-1")}");

    var callbackEcho = host.GetGrain<IEchoGrain>("callback");
    var callbackObserver = new RecordingEchoObserver();
    await using var callbackLease = host.CreateObjectReference<IEchoObserver>(
        callbackType: "echo-observer",
        implementation: callbackObserver);

    Console.WriteLine();
    TraceLog.Write("app", "move callback owner to remote node dev-node-2, then let the remote grain call back into a source-node callback target");
    await host.SetOwnerAsync<IEchoGrain>("callback", "dev-node-2");
    var callbackResult = await callbackEcho.PingWithObserverAsync("callback-roundtrip", callbackLease.Handle);
    var callbackMessages = callbackObserver.Describe();

    var staleCallbackHandle = callbackLease.Handle;
    await callbackLease.DisposeAsync();

    string callbackAfterDispose;
    try
    {
        await callbackEcho.PingWithObserverAsync("callback-after-dispose", staleCallbackHandle);
        callbackAfterDispose = "<unexpected-success>";
    }
    catch (InvalidOperationException exception)
    {
        callbackAfterDispose = exception.Message;
    }

    Console.WriteLine();
    TraceLog.Write("result", $"echo-callback-result = {callbackResult}");
    TraceLog.Write("result", $"echo-callback-messages = {callbackMessages}");
    TraceLog.Write("result", $"echo-callback-after-dispose = {callbackAfterDispose}");

    var reentrantCallbackEcho = host.GetGrain<IEchoGrain>("callback-reentrant");
    var reentrantObserver = new ReentrantCallbackEchoObserver(reentrantCallbackEcho);
    await using var reentrantCallbackLease = host.CreateObjectReference<IEchoObserver>(
        callbackType: "echo-observer",
        implementation: reentrantObserver);

    Console.WriteLine();
    TraceLog.Write(
        "app",
        "move callback-reentrant owner to remote node dev-node-2, then let the callback target reenter the original grain on the same request chain");
    await host.SetOwnerAsync<IEchoGrain>("callback-reentrant", "dev-node-2");
    var reentrantCallbackResult = await reentrantCallbackEcho.PingWithObserverAsync(
        "callback-reentrant",
        reentrantCallbackLease.Handle);

    Console.WriteLine();
    TraceLog.Write("result", $"echo-callback-reentrant-result = {reentrantCallbackResult}");
    TraceLog.Write("result", $"echo-callback-reentrant-observed = {reentrantObserver.DescribeObserved()}");
    TraceLog.Write("result", $"echo-callback-reentrant-nested = {reentrantObserver.DescribeNestedResults()}");

    var timerEcho = host.GetGrain<IEchoGrain>("timer-hold");
    Console.WriteLine();
    TraceLog.Write(
        "app",
        "arm an activation-owned timer during an exclusive turn; the timer callback should wait behind the active turn instead of bypassing it");
    var timerHoldResult = await timerEcho.HoldTurnWithTimerAsync("during-hold", holdDelayMs: 150, timerDelayMs: 30);
    await PauseAsync(TimeSpan.FromMilliseconds(100));
    var timerSnapshot = await timerEcho.GetTimerSnapshotAsync();

    Console.WriteLine();
    TraceLog.Write("result", $"echo-timer-hold-result = {timerHoldResult}");
    TraceLog.Write("result", $"echo-timer-snapshot = {timerSnapshot}");

    var timerDeactivateEcho = host.GetGrain<IEchoGrain>("timer-deactivate");
    Console.WriteLine();
    TraceLog.Write(
        "app",
        "arm an activation-owned timer, then deactivate the grain before it fires; the timer should be cancelled with the activation");
    await timerDeactivateEcho.ArmOneShotTimerAsync("cancelled", 80);
    await host.DeactivateGrainAsync<IEchoGrain>("timer-deactivate");
    await PauseAsync(TimeSpan.FromMilliseconds(140));
    var timerAfterDeactivate = await host.GetGrain<IEchoGrain>("timer-deactivate").GetTimerSnapshotAsync();

    Console.WriteLine();
    TraceLog.Write("result", $"echo-timer-after-deactivate = {timerAfterDeactivate}");

    host.FailNextProbe("dev-node-2", "heartbeat miss #1");
    await host.RunProbeTickAsync();
    LogDeliveries("fanout-1", await host.RunGossipTickAsync());

    Console.WriteLine();
    LogMembershipViews("membership-after-fanout-1");

    host.FailNextProbe("dev-node-2", "heartbeat miss #2");
    await host.RunProbeTickAsync();
    LogDeliveries("fanout-2", await host.RunGossipTickAsync());

    Console.WriteLine();
    LogMembershipViews("membership-after-fanout-2");

    TraceLog.Write("app", $"wait for stabilization window {stabilizationWindow}");
    await PauseAsync(stabilizationWindow + TimeSpan.FromMilliseconds(50));
    LogDeliveries("anti-entropy", await host.RunGossipTickAsync());

    Console.WriteLine();
    LogMembershipViews("membership-after-anti-entropy");
    LogDirectoryState("directory-before-runtime-checkpoint");

    var counterBeforeCheckpointFirst = await counter.AddAsync(3);

    Console.WriteLine();

    var counterBeforeCheckpointSecond = await counter.AddAsync(2);

    Console.WriteLine();
    LogActivationMetadata("activation-metadata-before-runtime-checkpoint");

    var checkpoint = host.CaptureRuntimeCheckpoint();
    TraceLog.Write(
        "result",
        $"runtime-checkpoint epoch={checkpoint.Membership.ClusterMembership.CurrentEpoch} directory-records={checkpoint.GrainDirectory.Records.Count} activation-directories={checkpoint.ActivationDirectories.Count} gossip-tick={checkpoint.Membership.Dissemination.TickNumber}");

    Console.WriteLine();
    TraceLog.Write("app", "dispose host and rebuild from runtime checkpoint");
    await host.DisposeAsync();
    host = CreateHost(checkpoint);

    TraceLog.Write("app", "restart host complete from runtime checkpoint");
    LogMembershipViews("membership-after-restart");
    LogDirectoryState("directory-after-restart");
    LogActivationMetadata("activation-metadata-after-restart");
    LogDeliveries("restart-gossip", await host.RunGossipTickAsync());

    var echoAfterRestart = host.GetGrain<IEchoGrain>("alpha");
    TraceLog.Write("app", "call echo after restart to prove recovered directory record can relocate without rebuilding from empty state");
    var recoveredEcho = await echoAfterRestart.PingAsync("after-runtime-checkpoint");

    Console.WriteLine();
    TraceLog.Write("result", $"echo-after-runtime-checkpoint = {recoveredEcho}");
    LogDirectoryState("directory-after-restart-relocation");

    var recoveredCounter = host.GetGrain<ICounterGrain>("beta");
    var counterAfterRestart = await recoveredCounter.AddAsync(4);

    Console.WriteLine();
    LogActivationMetadata("activation-metadata-after-counter-rehydrate");
    LogPlacementLoad("placement-load-before-new-placement");

    var freshEcho = host.GetGrain<IEchoGrain>("fresh-placement");
    var freshEchoResult = await freshEcho.PingAsync("initial-placement");
    var preferredLocalCounter = host.GetGrain<ICounterGrain>("prefer-local-placement");
    var preferredLocalCounterResult = await preferredLocalCounter.AddAsync(1);

    Console.WriteLine();
    TraceLog.Write("result", $"echo-fresh-initial-placement = {freshEchoResult}");
    TraceLog.Write("result", $"counter-prefer-local-placement = {preferredLocalCounterResult}");
    LogDirectoryState("directory-after-initial-placement");
    LogPlacementLoad("placement-load-after-initial-placement");

    var interleavingEcho = host.GetGrain<IEchoGrain>("interleaving");
    Console.WriteLine();
    TraceLog.Write(
        "app",
        "run two PingSlowAsync turns against the same echo activation; generated metadata allows this method to interleave");
    var interleavingStopwatch = Stopwatch.StartNew();
    var interleavingFirst = interleavingEcho.PingSlowAsync("interleave-a", 150);
    await PauseAsync(TimeSpan.FromMilliseconds(20));
    var interleavingSecond = interleavingEcho.PingSlowAsync("interleave-b", 150);
    var interleavingResults = await Task.WhenAll(interleavingFirst, interleavingSecond);
    interleavingStopwatch.Stop();

    Console.WriteLine();
    TraceLog.Write("result", $"echo-interleave-a = {interleavingResults[0]}");
    TraceLog.Write("result", $"echo-interleave-b = {interleavingResults[1]}");
    TraceLog.Write("result", $"echo-interleave-elapsed-ms = {interleavingStopwatch.ElapsedMilliseconds}");

    var reentrantEcho = host.GetGrain<IEchoGrain>("reentrant-self");
    Console.WriteLine();
    TraceLog.Write(
        "app",
        "run a same-grain self-call chain; request-chain reentrancy should let an exclusive activation call back into itself without deadlocking");
    var reentrantSelfResult = await reentrantEcho.ReentrantSelfCallAsync(2);

    Console.WriteLine();
    TraceLog.Write("result", $"echo-reentrant-self = {reentrantSelfResult}");

    TraceLog.Write("app", "rebalance echo/alpha and carry warm handoff state into the new activation");
    var handoffPerformed = await host.RebalanceGrainAsync<IEchoGrain>("alpha");
    var alphaAfterHandoff = await echoAfterRestart.PingAsync("after-handoff");

    Console.WriteLine();
    TraceLog.Write("result", $"handoff-alpha = {handoffPerformed}");
    TraceLog.Write("result", $"echo-alpha-after-handoff = {alphaAfterHandoff}");
    LogDirectoryState("directory-after-handoff");
    LogPlacementLoad("placement-load-after-handoff");

    var fallbackEcho = host.GetGrain<IEchoGrain>("fallback");
    var fallbackSeed = await fallbackEcho.PingAsync("fallback-seed");

    Console.WriteLine();
    TraceLog.Write("result", $"echo-fallback-seed = {fallbackSeed}");
    TraceLog.Write("app", "inject warm handoff apply failure for echo/fallback and fall back to cold activation");
    EchoGrain.FailNextWarmHandoffApply("simulated warm handoff apply failure");
    await host.SetOwnerAsync<IEchoGrain>("fallback", "dev-node-3");
    var fallbackAfterApplyFailure = await fallbackEcho.PingAsync("after-failed-apply");

    Console.WriteLine();
    TraceLog.Write("result", $"echo-fallback-after-failed-apply = {fallbackAfterApplyFailure}");

    TraceLog.Write("app", "inject warm handoff capture failure for echo/fallback and fall back to cold handoff");
    EchoGrain.FailNextWarmHandoffCapture("simulated warm handoff capture failure");
    await host.SetOwnerAsync<IEchoGrain>("fallback", "dev-node-1");
    var fallbackAfterCaptureFailure = await fallbackEcho.PingAsync("after-failed-capture");

    Console.WriteLine();
    TraceLog.Write("result", $"echo-fallback-after-failed-capture = {fallbackAfterCaptureFailure}");

    var drainEcho = host.GetGrain<IEchoGrain>("drain");
    TraceLog.Write("app", "start a slow echo turn, then move owner while the old activation is still busy");
    var inFlightDrainTurn = drainEcho.PingSlowAsync("drain-turn", 150);
    await PauseAsync(TimeSpan.FromMilliseconds(30));
    var drainMove = host.SetOwnerAsync<IEchoGrain>("drain", "dev-node-3").AsTask();
    await Task.WhenAll(inFlightDrainTurn, drainMove);
    var drainAfterHandoff = await drainEcho.PingAsync("after-drain-handoff");

    Console.WriteLine();
    TraceLog.Write("result", $"echo-drain-in-flight = {inFlightDrainTurn.Result}");
    TraceLog.Write("result", $"echo-drain-after-handoff = {drainAfterHandoff}");

    TraceLog.Write("app", "wait for counter to become idle; echo keeps a longer per-grain collection age");
    await PauseAsync(TimeSpan.FromMilliseconds(150));

    var collectedIdle = await host.CollectIdleGrainsAsync(TimeSpan.FromMilliseconds(300));

    Console.WriteLine();
    TraceLog.Write("result", $"idle-collected = {collectedIdle}");

    Console.WriteLine();
    LogActivationMetadata("activation-metadata-after-idle-collect");

    var echoAfterIdleCollect = await echoAfterRestart.PingAsync("after-idle-collect");
    var counterAfterIdleCollect = await recoveredCounter.AddAsync(2);

    Console.WriteLine();
    TraceLog.Write("result", $"echo-after-idle-collect = {echoAfterIdleCollect}");
    TraceLog.Write("result", $"counter-after-idle-collect = {counterAfterIdleCollect}");
    TraceLog.Write("result", $"response-disposition-after-runtime-checkpoint[dev-node-1] = {host.DescribeResponseDisposition("dev-node-1")}");
}
finally
{
    await host.DisposeAsync();
}

OrleansReplicaKernelHost CreateHost(OrleansReplicaKernelRuntimeCheckpoint? checkpoint = null)
{
    var builder = new OrleansReplicaKernelBuilder()
        .AddGeneratedGrainImplementationsFromAssembly(typeof(EchoGrain).Assembly)
        .WithMembershipStabilizationWindow(stabilizationWindow)
        .WithMembershipGossipFanout(gossipFanout)
        .WithMembershipAntiEntropyInterval(antiEntropyInterval)
        .AddGeneratedGrainReferencesFromAssembly(typeof(EchoGrainReference).Assembly)
        .AddGeneratedObjectReferencesFromAssembly(typeof(EchoObserverReference).Assembly);

    if (checkpoint is not null)
    {
        builder.WithRuntimeCheckpoint(checkpoint);
    }

    return builder.Build("dev-node-1", "dev-node-2", "dev-node-3");
}

CancellationTokenSource CreateTimeout(TimeSpan delay) => new(delay, host.TimeProvider);

Task PauseAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
    Task.Delay(delay, host.TimeProvider, cancellationToken);

void LogMembershipViews(string label)
{
    foreach (var observerNodeName in new[] { "dev-node-1", "dev-node-2", "dev-node-3" })
    {
        TraceLog.Write("result", $"{label}[{observerNodeName}] = {host.DescribeMembershipView(observerNodeName)}");
    }
}

void LogDeliveries(string label, IReadOnlyList<MembershipGossipDelivery> deliveries)
{
    if (deliveries.Count == 0)
    {
        TraceLog.Write("result", $"{label}-deliveries = <none>");
        return;
    }

    var rendered = string.Join(
        "; ",
        deliveries.Select(
            item => $"{item.ObserverNodeName}:{item.Mode}[{item.FromExclusiveEpoch}->{item.ToInclusiveEpoch}] tick={item.TickNumber} changes={item.ConsumedChanges} stabilized={item.StabilizedNodes}"));
    TraceLog.Write("result", $"{label}-deliveries = {rendered}");
}

void LogDirectoryState(string label)
{
    TraceLog.Write("result", $"{label} = {host.DescribeGrainDirectory()}");
}

void LogActivationMetadata(string label)
{
    foreach (var nodeName in new[] { "dev-node-1", "dev-node-2", "dev-node-3" })
    {
        TraceLog.Write("result", $"{label}[{nodeName}] = {host.DescribeActivationMetadata(nodeName)}");
    }
}

void LogPlacementLoad(string label)
{
    TraceLog.Write("result", $"{label} = {host.DescribePlacementLoad()}");
}
