using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Demo;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Tests.TestSupport;

namespace OrleansReplicaKernel.Tests.Runtime;

public sealed class GrainLifecycleIntegrationTests
{
    [Fact]
    public async Task LifecycleHooks_RunOnFirstActivation_AndIdleCollection()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        var storagePath = CreateStoragePath("idle");

        try
        {
            await using var host = CreateHost(timeProvider, storagePath);
            var grain = host.GetGrain<ILifecycleProbeGrain>("idle");

            Assert.Equal(1, await grain.GetActivationCountAsync());
            Assert.Equal(0, await grain.GetDeactivationCountAsync());
            Assert.Equal("<none>", await grain.GetLastDeactivationReasonAsync());

            var collected = await host.CollectIdleGrainsAsync(TimeSpan.Zero);
            Assert.Equal(1, collected);

            grain = host.GetGrain<ILifecycleProbeGrain>("idle");

            Assert.Equal(2, await grain.GetActivationCountAsync());
            Assert.Equal(1, await grain.GetDeactivationCountAsync());
            Assert.Equal(
                nameof(ActivationDeactivationReason.IdleCollection),
                await grain.GetLastDeactivationReasonAsync());
        }
        finally
        {
            DeleteStorageArtifacts(storagePath);
        }
    }

    [Fact]
    public async Task LifecycleHooks_RunOnHandoffDeactivate()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        var storagePath = CreateStoragePath("handoff");

        try
        {
            await using var host = CreateHost(timeProvider, storagePath, "dev-node-2");
            var grain = host.GetGrain<ILifecycleProbeGrain>("handoff");

            Assert.Equal(1, await grain.GetActivationCountAsync());

            await host.SetOwnerAsync<ILifecycleProbeGrain>("handoff", "dev-node-2");

            grain = host.GetGrain<ILifecycleProbeGrain>("handoff");

            Assert.Equal(2, await grain.GetActivationCountAsync());
            Assert.Equal(1, await grain.GetDeactivationCountAsync());
            Assert.Equal(
                nameof(ActivationDeactivationReason.Handoff),
                await grain.GetLastDeactivationReasonAsync());
        }
        finally
        {
            DeleteStorageArtifacts(storagePath);
        }
    }

    [Fact]
    public async Task ActivationHookFailure_CleansFailedActivation_AndAllowsRetry()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero));
        var storagePath = CreateStoragePath("activation-failure");

        try
        {
            await using var host = CreateHost(timeProvider, storagePath);
            var grain = host.GetGrain<IActivationFailureGrain>("retry");

            await Assert.ThrowsAsync<ActivationInitializationException>(() => grain.GetActivationCountAsync());

            Assert.Equal(1, await host.GetGrain<IActivationFailureGrain>("retry").GetActivationCountAsync());
        }
        finally
        {
            DeleteStorageArtifacts(storagePath);
        }
    }

    private static OrleansReplicaKernelHost CreateHost(
        TimeProvider timeProvider,
        string storagePath,
        params string[] peerNodeNames)
        => new OrleansReplicaKernelBuilder()
            .AddGeneratedGrainImplementationsFromAssembly(typeof(EchoGrain).Assembly)
            .AddGeneratedGrainReferencesFromAssembly(typeof(EchoGrainReference).Assembly)
            .WithTimeProvider(timeProvider)
            .UseFileGrainStorage(storagePath)
            .Build("dev-node-1", peerNodeNames);

    private static string CreateStoragePath(string prefix)
        => Path.Combine(
            Path.GetTempPath(),
            "OrleansReplicaKernel.Tests",
            "grain-lifecycle",
            prefix + "-" + Guid.NewGuid().ToString("N") + ".json");

    private static void DeleteStorageArtifacts(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var lockPath = path + ".lock";
        if (File.Exists(lockPath))
        {
            File.Delete(lockPath);
        }
    }
}
