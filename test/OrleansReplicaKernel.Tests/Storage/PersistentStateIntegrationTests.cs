using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Demo;

namespace OrleansReplicaKernel.Tests.Storage;

public sealed class PersistentStateIntegrationTests
{
    [Fact]
    public async Task PersistentState_IsAutoReadAfterDeactivateAndReactivate()
    {
        var primaryStoragePath = CreateStoragePath("primary");
        var secondaryStoragePath = CreateStoragePath("secondary-unused");

        try
        {
            await using var host = CreateHost(primaryStoragePath, secondaryStoragePath);
            var grain = host.GetGrain<IPersistentCounterGrain>("alpha");

            var updated = await grain.AddAsync(4);
            await host.DeactivateGrainAsync<IPersistentCounterGrain>("alpha");
            var reactivated = await host.GetGrain<IPersistentCounterGrain>("alpha").GetValueAsync();

            Assert.Equal(4, updated);
            Assert.Equal(4, reactivated);
        }
        finally
        {
            DeleteStorageArtifacts(primaryStoragePath);
            DeleteStorageArtifacts(secondaryStoragePath);
        }
    }

    [Fact]
    public async Task PersistentState_SupportsMultipleIndependentStates()
    {
        var primaryStoragePath = CreateStoragePath("primary");
        var secondaryStoragePath = CreateStoragePath("secondary");

        try
        {
            await using var host = CreateHost(primaryStoragePath, secondaryStoragePath);
            var grain = host.GetGrain<IMultiStateGrain>("alpha");

            await grain.SetPrimaryAsync("left");
            await grain.SetSecondaryAsync("right");
            await host.DeactivateGrainAsync<IMultiStateGrain>("alpha");

            var snapshot = await host.GetGrain<IMultiStateGrain>("alpha").GetSnapshotAsync();

            Assert.Equal("left|right", snapshot);
        }
        finally
        {
            DeleteStorageArtifacts(primaryStoragePath);
            DeleteStorageArtifacts(secondaryStoragePath);
        }
    }

    private static OrleansReplicaKernelHost CreateHost(string primaryStoragePath, string secondaryStoragePath)
        => new OrleansReplicaKernelBuilder()
            .AddGeneratedGrainImplementationsFromAssembly(typeof(EchoGrain).Assembly)
            .AddGeneratedGrainReferencesFromAssembly(typeof(EchoGrainReference).Assembly)
            .UseFileGrainStorage(primaryStoragePath)
            .UseFileGrainStorage("secondary", secondaryStoragePath)
            .Build("dev-node-1");

    private static string CreateStoragePath(string prefix)
        => Path.Combine(
            Path.GetTempPath(),
            "OrleansReplicaKernel.Tests",
            "persistent-state",
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
