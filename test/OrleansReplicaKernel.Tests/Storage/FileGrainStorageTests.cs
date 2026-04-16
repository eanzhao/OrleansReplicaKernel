using OrleansReplicaKernel.Demo;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Storage;

namespace OrleansReplicaKernel.Tests.Storage;

public sealed class FileGrainStorageTests
{
    [Fact]
    public async Task PersistentState_WriteThenRead_CanRoundTripThroughFileStorage()
    {
        var path = CreateStoragePath();

        try
        {
            var storage = new FileGrainStorage(path);
            var grainId = new GrainId("persistentCounter", "roundtrip");
            var writer = new PersistentState<PersistentCounterGrainState>("counter", grainId, storage);

            writer.State.Total = 7;
            await writer.WriteStateAsync();

            var reader = new PersistentState<PersistentCounterGrainState>("counter", grainId, storage);
            await reader.ReadStateAsync();

            Assert.True(reader.RecordExists);
            Assert.Equal(7, reader.State.Total);
            Assert.NotNull(reader.ETag);
        }
        finally
        {
            DeleteStorageArtifacts(path);
        }
    }

    [Fact]
    public async Task PersistentState_WriteStateAsync_DetectsStaleETagConflicts()
    {
        var path = CreateStoragePath();

        try
        {
            var storage = new FileGrainStorage(path);
            var grainId = new GrainId("persistentCounter", "etag-conflict");
            var first = new PersistentState<PersistentCounterGrainState>("counter", grainId, storage);
            var second = new PersistentState<PersistentCounterGrainState>("counter", grainId, storage);

            await first.ReadStateAsync();
            await second.ReadStateAsync();

            first.State.Total = 1;
            await first.WriteStateAsync();

            second.State.Total = 2;
            var exception = await Assert.ThrowsAsync<InconsistentStateException>(
                () => second.WriteStateAsync().AsTask());

            Assert.Equal(grainId, exception.GrainId);
            Assert.Equal("counter", exception.StateName);
            Assert.Null(exception.ExpectedETag);
            Assert.NotNull(exception.ActualETag);
        }
        finally
        {
            DeleteStorageArtifacts(path);
        }
    }

    private static string CreateStoragePath()
        => Path.Combine(
            Path.GetTempPath(),
            "OrleansReplicaKernel.Tests",
            "grain-storage",
            Guid.NewGuid().ToString("N") + ".json");

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
