using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Demo;
using OrleansReplicaKernel.Routing;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Tests.TestSupport;

namespace OrleansReplicaKernel.Tests.Runtime;

public sealed class RuntimeCheckpointStateTests
{
    [Fact]
    public async Task RuntimeCheckpoint_RestoresRuntimeMetadataWithoutRehydratingActivationState()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        OrleansReplicaKernelHost? host = CreateHost(timeProvider, checkpoint: null);

        try
        {
            var seeded = await host.GetGrain<IEchoGrain>("runtime-checkpoint-metadata").PingAsync("seed");
            var beforeCheckpoint = host.CaptureRuntimeCheckpoint();

            Assert.Equal("echo:seed:count=1", seeded);
            Assert.Single(beforeCheckpoint.GrainDirectory.Records);
            Assert.Single(beforeCheckpoint.ActivationDirectories);
            Assert.Single(beforeCheckpoint.ActivationDirectories[0].Records);

            await host.DisposeAsync();
            host = null;

            host = CreateHost(timeProvider, beforeCheckpoint);

            var restoredCheckpoint = host.CaptureRuntimeCheckpoint();

            Assert.Equal(beforeCheckpoint.GrainDirectory.Records, restoredCheckpoint.GrainDirectory.Records);
            AssertActivationDirectoriesEqual(beforeCheckpoint.ActivationDirectories, restoredCheckpoint.ActivationDirectories);

            var recovered = await host.GetGrain<IEchoGrain>("runtime-checkpoint-metadata").PingAsync("after-runtime-checkpoint");

            Assert.Equal("echo:after-runtime-checkpoint:count=1", recovered);
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
    public async Task RuntimeCheckpoint_PreservesRecoveredOwnerAssignmentAcrossRestore()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        OrleansReplicaKernelHost? host = CreateHost(timeProvider, checkpoint: null, "dev-node-2");

        try
        {
            var grain = host.GetGrain<IEchoGrain>("runtime-checkpoint-owner");

            var seeded = await grain.PingAsync("seed");
            await host.SetOwnerAsync<IEchoGrain>("runtime-checkpoint-owner", "dev-node-2");
            var afterMove = await grain.PingAsync("after-move");
            var beforeCheckpoint = host.CaptureRuntimeCheckpoint();
            var beforeRecord = Assert.Single(beforeCheckpoint.GrainDirectory.Records);

            Assert.Equal("echo:seed:count=1", seeded);
            Assert.Equal("echo:after-move:count=2", afterMove);
            Assert.Equal("dev-node-2", beforeRecord.OwnerNodeName);
            Assert.Contains(
                beforeCheckpoint.ActivationDirectories.Single(directory => directory.NodeName == "dev-node-2").Records,
                record => record.GrainId == beforeRecord.GrainId);

            await host.DisposeAsync();
            host = null;

            host = CreateHost(timeProvider, beforeCheckpoint, "dev-node-2");

            var restoredCheckpoint = host.CaptureRuntimeCheckpoint();
            var restoredRecord = Assert.Single(restoredCheckpoint.GrainDirectory.Records);

            Assert.Equal(beforeCheckpoint.GrainDirectory.Records, restoredCheckpoint.GrainDirectory.Records);
            AssertActivationDirectoriesEqual(beforeCheckpoint.ActivationDirectories, restoredCheckpoint.ActivationDirectories);
            Assert.Equal("dev-node-2", restoredRecord.OwnerNodeName);

            var recovered = await grain.PingAsync("after-restore");

            Assert.Equal("echo:after-restore:count=1", recovered);
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
        OrleansReplicaKernelRuntimeCheckpoint? checkpoint,
        params string[] peerNodeNames)
    {
        var builder = new OrleansReplicaKernelBuilder()
            .AddGeneratedGrainImplementationsFromAssembly(typeof(EchoGrain).Assembly)
            .AddGeneratedGrainReferencesFromAssembly(typeof(EchoGrainReference).Assembly)
            .AddGeneratedObjectReferencesFromAssembly(typeof(EchoObserverReference).Assembly)
            .WithTimeProvider(timeProvider);

        if (checkpoint is not null)
        {
            builder.WithRuntimeCheckpoint(checkpoint);
        }

        return builder.Build("dev-node-1", peerNodeNames);
    }

    private static void AssertActivationDirectoriesEqual(
        IReadOnlyList<ActivationDirectoryCheckpoint> expected,
        IReadOnlyList<ActivationDirectoryCheckpoint> actual)
    {
        Assert.Equal(expected.Select(item => item.NodeName), actual.Select(item => item.NodeName));

        foreach (var (expectedDirectory, actualDirectory) in expected.Zip(actual))
        {
            Assert.Equal(expectedDirectory.Records, actualDirectory.Records);
        }
    }
}
