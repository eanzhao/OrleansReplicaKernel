using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Demo;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Tests.TestSupport;

namespace OrleansReplicaKernel.Tests.Runtime;

public sealed class RuntimeCheckpointMembershipTests
{
    [Fact]
    public async Task RuntimeCheckpoint_RestoresMembershipFanoutCursorAndPendingStabilization()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        OrleansReplicaKernelHost? host = CreateHost(
            timeProvider,
            runtimeCheckpoint: null,
            membershipCheckpoint: null,
            stabilizationWindow: TimeSpan.FromSeconds(5),
            fanout: 1,
            antiEntropyInterval: 10,
            "dev-node-2");

        try
        {
            var initialPrimary = host.GetMembershipViewSnapshot("dev-node-1").Single(member => member.NodeName == "dev-node-2");
            var initialSecondary = host.GetMembershipViewSnapshot("dev-node-2").Single(member => member.NodeName == "dev-node-2");

            Assert.Equal(NodeHealthStatus.Healthy, initialPrimary.StableStatus);
            Assert.Equal(NodeHealthStatus.Healthy, initialPrimary.ObservedStatus);
            Assert.Equal(NodeHealthStatus.Healthy, initialSecondary.StableStatus);
            Assert.Equal(NodeHealthStatus.Healthy, initialSecondary.ObservedStatus);

            await host.SetNodeHealthAsync("dev-node-2", NodeHealthStatus.Suspect);
            var beforeCheckpoint = await host.RunGossipTickAsync();

            Assert.Single(beforeCheckpoint);
            Assert.Equal("fanout", beforeCheckpoint[0].Mode);
            Assert.Equal("dev-node-1", beforeCheckpoint[0].ObserverNodeName);

            var primaryBefore = host.GetMembershipViewSnapshot("dev-node-1").Single(member => member.NodeName == "dev-node-2");
            var secondaryBefore = host.GetMembershipViewSnapshot("dev-node-2").Single(member => member.NodeName == "dev-node-2");

            Assert.Equal(NodeHealthStatus.Healthy, primaryBefore.StableStatus);
            Assert.Equal(NodeHealthStatus.Suspect, primaryBefore.ObservedStatus);
            Assert.Equal(NodeHealthStatus.Healthy, secondaryBefore.StableStatus);
            Assert.Equal(NodeHealthStatus.Healthy, secondaryBefore.ObservedStatus);

            var checkpoint = host.CaptureRuntimeCheckpoint();

            await host.DisposeAsync();
            host = null;

            host = CreateHost(
                timeProvider,
                runtimeCheckpoint: checkpoint,
                membershipCheckpoint: null,
                stabilizationWindow: TimeSpan.FromSeconds(5),
                fanout: 1,
                antiEntropyInterval: 10,
                "dev-node-2");

            timeProvider.Advance(TimeSpan.FromSeconds(5));
            var afterRestore = await host.RunGossipTickAsync();

            Assert.Equal(2, afterRestore.Count);
            Assert.Contains(
                afterRestore,
                delivery => delivery.ObserverNodeName == "dev-node-2"
                    && delivery.Mode == "fanout"
                    && delivery.ConsumedChanges == 1
                    && delivery.StabilizedNodes == 1);
            Assert.Contains(
                afterRestore,
                delivery => delivery.ObserverNodeName == "dev-node-1"
                    && delivery.Mode == "stabilization"
                    && delivery.ConsumedChanges == 0
                    && delivery.StabilizedNodes == 1);

            var primaryAfter = host.GetMembershipViewSnapshot("dev-node-1").Single(member => member.NodeName == "dev-node-2");
            var secondaryAfter = host.GetMembershipViewSnapshot("dev-node-2").Single(member => member.NodeName == "dev-node-2");

            Assert.Equal(NodeHealthStatus.Suspect, primaryAfter.StableStatus);
            Assert.Equal(NodeHealthStatus.Suspect, primaryAfter.ObservedStatus);
            Assert.Equal(NodeHealthStatus.Suspect, secondaryAfter.StableStatus);
            Assert.Equal(NodeHealthStatus.Suspect, secondaryAfter.ObservedStatus);
        }
        finally
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task MembershipCheckpoint_RestoresMembershipContinuityWithoutRehydratingRuntimeState()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        OrleansReplicaKernelHost? host = CreateHost(
            timeProvider,
            runtimeCheckpoint: null,
            membershipCheckpoint: null,
            stabilizationWindow: TimeSpan.FromSeconds(5),
            fanout: 1,
            antiEntropyInterval: 10,
            "dev-node-2");

        try
        {
            var grain = host.GetGrain<IEchoGrain>("membership-checkpoint-only");
            var seeded = await grain.PingAsync("seed");

            Assert.Equal("echo:seed:count=1", seeded);
            Assert.NotEqual("<empty>", host.DescribeGrainDirectory());

            await host.SetNodeHealthAsync("dev-node-2", NodeHealthStatus.Suspect);
            var beforeCheckpoint = await host.RunGossipTickAsync();

            Assert.Single(beforeCheckpoint);
            Assert.Equal("fanout", beforeCheckpoint[0].Mode);
            Assert.Equal("dev-node-1", beforeCheckpoint[0].ObserverNodeName);

            var membershipCheckpoint = host.CaptureMembershipCheckpoint();

            await host.DisposeAsync();
            host = null;

            host = CreateHost(
                timeProvider,
                runtimeCheckpoint: null,
                membershipCheckpoint,
                stabilizationWindow: TimeSpan.FromSeconds(5),
                fanout: 1,
                antiEntropyInterval: 10,
                "dev-node-2");

            Assert.Equal("<empty>", host.DescribeGrainDirectory());

            timeProvider.Advance(TimeSpan.FromSeconds(5));
            var afterRestore = await host.RunGossipTickAsync();

            Assert.Equal(2, afterRestore.Count);
            Assert.Contains(
                afterRestore,
                delivery => delivery.ObserverNodeName == "dev-node-2"
                    && delivery.Mode == "fanout"
                    && delivery.ConsumedChanges == 1
                    && delivery.StabilizedNodes == 1);
            Assert.Contains(
                afterRestore,
                delivery => delivery.ObserverNodeName == "dev-node-1"
                    && delivery.Mode == "stabilization"
                    && delivery.ConsumedChanges == 0
                    && delivery.StabilizedNodes == 1);

            var recovered = await host.GetGrain<IEchoGrain>("membership-checkpoint-only").PingAsync("after-membership-checkpoint");

            Assert.Equal("echo:after-membership-checkpoint:count=1", recovered);
        }
        finally
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }
        }
    }

    private static OrleansReplicaKernelHost CreateHost(
        TimeProvider timeProvider,
        OrleansReplicaKernelRuntimeCheckpoint? runtimeCheckpoint,
        OrleansReplicaKernelMembershipCheckpoint? membershipCheckpoint,
        TimeSpan stabilizationWindow,
        int fanout,
        int antiEntropyInterval,
        params string[] peerNodeNames)
    {
        var builder = new OrleansReplicaKernelBuilder()
            .AddGeneratedGrainImplementationsFromAssembly(typeof(EchoGrain).Assembly)
            .AddGeneratedGrainReferencesFromAssembly(typeof(EchoGrainReference).Assembly)
            .AddGeneratedObjectReferencesFromAssembly(typeof(EchoObserverReference).Assembly)
            .WithTimeProvider(timeProvider)
            .WithMembershipStabilizationWindow(stabilizationWindow)
            .WithMembershipGossipFanout(fanout)
            .WithMembershipAntiEntropyInterval(antiEntropyInterval);

        if (runtimeCheckpoint is not null)
        {
            builder.WithRuntimeCheckpoint(runtimeCheckpoint);
        }
        else if (membershipCheckpoint is not null)
        {
            builder.WithMembershipCheckpoint(membershipCheckpoint);
        }

        return builder.Build("dev-node-1", peerNodeNames);
    }
}
