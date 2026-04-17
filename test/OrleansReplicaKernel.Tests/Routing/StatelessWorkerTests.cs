using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Routing;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Scheduling;
using OrleansReplicaKernel.Security;

namespace OrleansReplicaKernel.Tests.Routing;

public sealed class StatelessWorkerTests
{
    private const string NodeName = "test-node";

    [Fact]
    public void StatelessWorkerPool_CreatesMultipleActivations_UpToMax()
    {
        var pool = new LocalActivationDirectory.StatelessWorkerPool(maxWorkers: 3);
        var created = new HashSet<ActivationEntry>();
        var grainId = new GrainId("Worker", "key1");

        for (var i = 0; i < 5; i++)
        {
            var entry = pool.SelectOrCreate(() => CreateEntry(grainId));
            created.Add(entry);
        }

        Assert.Equal(3, pool.Count);
        Assert.Equal(3, created.Count);
    }

    [Fact]
    public void StatelessWorkerPool_RoundRobinsAcrossWorkers()
    {
        var pool = new LocalActivationDirectory.StatelessWorkerPool(maxWorkers: 2);
        var grainId = new GrainId("Worker", "key1");

        var first = pool.SelectOrCreate(() => CreateEntry(grainId));
        var second = pool.SelectOrCreate(() => CreateEntry(grainId));
        var third = pool.SelectOrCreate(() => CreateEntry(grainId));
        var fourth = pool.SelectOrCreate(() => CreateEntry(grainId));

        Assert.Equal(2, pool.Count);
        Assert.Equal(first, third);
        Assert.Equal(second, fourth);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void GetOrCreate_CreatesMultipleActivations_ForStatelessWorkerGrains()
    {
        var grainType = "StatelessEcho";
        var grainId = new GrainId(grainType, "key1");
        var directory = CreateDirectory(
            grainType,
            isStatelessWorker: true,
            maxLocalWorkers: 3);

        var activations = new HashSet<ActivationEntry>();
        for (var i = 0; i < 5; i++)
        {
            activations.Add(directory.GetOrCreate(new GrainAddress(NodeName, grainId, OwnerVersion: 1)));
        }

        Assert.Equal(3, activations.Count);
        Assert.Equal(3, directory.GetActivationCount());
    }

    [Fact]
    public void GetOrCreate_ReusesActivation_ForNonStatelessWorkerGrains()
    {
        var grainType = "RegularEcho";
        var grainId = new GrainId(grainType, "key1");
        var directory = CreateDirectory(
            grainType,
            isStatelessWorker: false,
            maxLocalWorkers: 0);

        var first = directory.GetOrCreate(new GrainAddress(NodeName, grainId, OwnerVersion: 1));
        var second = directory.GetOrCreate(new GrainAddress(NodeName, grainId, OwnerVersion: 1));

        Assert.Same(first, second);
        Assert.Equal(1, directory.GetActivationCount());
    }

    [Fact]
    public async Task DeactivateAll_IncludesStatelessWorkerActivations()
    {
        var grainType = "StatelessEcho";
        var grainId = new GrainId(grainType, "key1");
        var directory = CreateDirectory(
            grainType,
            isStatelessWorker: true,
            maxLocalWorkers: 3);

        for (var i = 0; i < 3; i++)
        {
            directory.GetOrCreate(new GrainAddress(NodeName, grainId, OwnerVersion: 1));
        }

        Assert.Equal(3, directory.GetActivationCount());

        var deactivated = await directory.DeactivateAllAsync();

        Assert.Equal(3, deactivated);
        Assert.Equal(0, directory.GetActivationCount());
    }

    private static LocalActivationDirectory CreateDirectory(
        string grainType,
        bool isStatelessWorker,
        int maxLocalWorkers)
    {
        var grainFactories = new Dictionary<string, Func<object>>(StringComparer.Ordinal)
        {
            [grainType] = () => new object()
        };
        var placementHints = new Dictionary<string, GrainTypePlacementHint>(StringComparer.Ordinal)
        {
            [grainType] = new(PreferLocalPlacement: true)
            {
                IsStatelessWorker = isStatelessWorker,
                MaxLocalWorkers = maxLocalWorkers
            }
        };

        return new LocalActivationDirectory(
            NodeName,
            grainFactories,
            new Dictionary<string, GrainTypeCollectionPolicy>(StringComparer.Ordinal),
            new LocalCallbackDirectory(),
            placementHints: placementHints);
    }

    private static ActivationEntry CreateEntry(GrainId grainId)
        => new(
            grainId,
            new object(),
            ownerVersion: 1,
            GrainTypeSchedulingPolicy.Default,
            TimeProvider.System);
}
