using System.Text.Json;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Scheduling;
using OrleansReplicaKernel.Security;
using OrleansReplicaKernel.Storage;
using OrleansReplicaKernel.Transactions;

namespace OrleansReplicaKernel.Tests.Runtime;

public sealed class GrainExtensionTests
{
    private const string NodeName = "test-node";

    [Fact]
    public async Task RegisteredExtension_TakesPrecedenceOverGrainImplementation_ForMatchingInterface()
    {
        var grainId = new GrainId("extension-target", "alpha");
        await using var activation = CreateActivation(
            grainId,
            new GrainOwnedExtensionContract("grain"));

        var beforeRegistration = await InvokeAsync<string>(activation, grainId, new GetValueInvokable());

        activation.RegisterExtension<ITestExtensionContract>(new TestExtension("extension"));

        var afterRegistration = await InvokeAsync<string>(activation, grainId, new GetValueInvokable());

        Assert.Equal("grain", beforeRegistration);
        Assert.Equal("extension", afterRegistration);
    }

    [Fact]
    public async Task ContextAwareExtension_CanAccessPersistentStateAcrossActivations()
    {
        var grainId = new GrainId("extension-state", "alpha");
        var storage = new InMemoryGrainStorage();

        await using (var firstActivation = CreateActivation(
                         grainId,
                         new object(),
                         CreateActivationContext(grainId, storage)))
        {
            firstActivation.RegisterExtension<IStatefulExtensionContract>(new StatefulExtension());

            var first = await InvokeAsync<int>(firstActivation, grainId, new IncrementStateInvokable(3));

            Assert.Equal(3, first);
        }

        await using var secondActivation = CreateActivation(
            grainId,
            new object(),
            CreateActivationContext(grainId, storage));
        secondActivation.RegisterExtension<IStatefulExtensionContract>(new StatefulExtension());

        var second = await InvokeAsync<int>(secondActivation, grainId, new IncrementStateInvokable(2));

        Assert.Equal(5, second);
    }

    private static ActivationEntry CreateActivation(
        GrainId grainId,
        object instance,
        GrainActivationContext? activationContext = null)
        => new(
            grainId,
            instance,
            ownerVersion: 0,
            GrainTypeSchedulingPolicy.Default,
            TimeProvider.System,
            activationContext);

    private static GrainActivationContext CreateActivationContext(GrainId grainId, IGrainStorage storage)
        => new(
            grainId,
            new PersistentStateFactory(
                new GrainStorageResolver(
                    storage,
                    new Dictionary<string, IGrainStorage>(StringComparer.Ordinal))),
            TransactionalStateFactory.Empty);

    private static async ValueTask<TResult> InvokeAsync<TResult>(
        ActivationEntry activation,
        GrainId grainId,
        IInvokable invokable)
        => (TResult)(await activation.InvokeAsync(
            new InvocationMessage(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                DateTimeOffset.UtcNow,
                NodeName,
                new GrainAddress(NodeName, grainId, OwnerVersion: 0),
                invokable,
                InvocationSourceKind.ClusterNode,
                InvocationIdentity.CreateLocal(NodeName, InvocationSourceKind.ClusterNode)),
            new TestInvocationRuntime(),
            CancellationToken.None))!;

    private interface ITestExtensionContract
    {
        ValueTask<string> GetValueAsync(CancellationToken cancellationToken = default);
    }

    private interface IStatefulExtensionContract
    {
        ValueTask<int> IncrementAsync(int value, CancellationToken cancellationToken = default);
    }

    private sealed class GrainOwnedExtensionContract : ITestExtensionContract
    {
        private readonly string _value;

        public GrainOwnedExtensionContract(string value)
        {
            _value = value;
        }

        public ValueTask<string> GetValueAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_value);
    }

    private sealed class TestExtension : ITestExtensionContract, IGrainExtension
    {
        private readonly string _value;

        public TestExtension(string value)
        {
            _value = value;
        }

        public ValueTask<string> GetValueAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_value);
    }

    private sealed class StatefulExtension :
        IStatefulExtensionContract,
        IGrainExtension,
        IGrainExtensionContextAware
    {
        private IGrainExtensionContext? _context;

        public void SetGrainExtensionContext(IGrainExtensionContext context)
        {
            _context = context;
        }

        public async ValueTask<int> IncrementAsync(int value, CancellationToken cancellationToken = default)
        {
            var context = _context
                ?? throw new InvalidOperationException("Stateful extension was not bound to a grain extension context.");
            var state = await context.GetPersistentStateAsync<ExtensionCounterState>(
                "extension",
                cancellationToken: cancellationToken);
            state.State.Total += value;
            await state.WriteStateAsync(cancellationToken);
            return state.State.Total;
        }
    }

    private sealed class GetValueInvokable : IInvokable
    {
        public string InterfaceName => nameof(ITestExtensionContract);

        public string InterfaceCompatibilityFamily => typeof(ITestExtensionContract).FullName!;

        public int InterfaceVersion => 1;

        public string MethodName => nameof(ITestExtensionContract.GetValueAsync);

        public async ValueTask<object?> InvokeAsync(object target, CancellationToken cancellationToken)
            => await ((ITestExtensionContract)target).GetValueAsync(cancellationToken);
    }

    private sealed class IncrementStateInvokable : IInvokable
    {
        private readonly int _value;

        public IncrementStateInvokable(int value)
        {
            _value = value;
        }

        public string InterfaceName => nameof(IStatefulExtensionContract);

        public string InterfaceCompatibilityFamily => typeof(IStatefulExtensionContract).FullName!;

        public int InterfaceVersion => 1;

        public string MethodName => nameof(IStatefulExtensionContract.IncrementAsync);

        public async ValueTask<object?> InvokeAsync(object target, CancellationToken cancellationToken)
            => await ((IStatefulExtensionContract)target).IncrementAsync(_value, cancellationToken);
    }

    private sealed class TestInvocationRuntime : IInvocationRuntime
    {
        public ValueTask<TResult> InvokeAsync<TResult>(
            GrainId grainId,
            IInvokable invokable,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Nested runtime invocations are not used by grain extension tests.");
    }

    private sealed class InMemoryGrainStorage : IGrainStorage
    {
        private readonly Dictionary<string, StoredState> _states = new(StringComparer.Ordinal);

        public ValueTask ReadStateAsync<TState>(
            string stateName,
            GrainId grainId,
            IGrainState<TState> grainState,
            CancellationToken cancellationToken = default)
        {
            if (_states.TryGetValue(CreateStorageKey(stateName, grainId), out var stored))
            {
                grainState.State = JsonSerializer.Deserialize<TState>(stored.Payload)
                    ?? throw new InvalidOperationException("Stored grain state deserialized to null.");
                grainState.ETag = stored.ETag;
                grainState.RecordExists = true;
            }
            else
            {
                grainState.State = CreateDefaultState<TState>();
                grainState.ETag = null;
                grainState.RecordExists = false;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask WriteStateAsync<TState>(
            string stateName,
            GrainId grainId,
            IGrainState<TState> grainState,
            CancellationToken cancellationToken = default)
        {
            var eTag = Guid.NewGuid().ToString("N");
            _states[CreateStorageKey(stateName, grainId)] = new StoredState(
                JsonSerializer.Serialize(grainState.State),
                eTag);
            grainState.ETag = eTag;
            grainState.RecordExists = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearStateAsync<TState>(
            string stateName,
            GrainId grainId,
            IGrainState<TState> grainState,
            CancellationToken cancellationToken = default)
        {
            _states.Remove(CreateStorageKey(stateName, grainId));
            grainState.State = CreateDefaultState<TState>();
            grainState.ETag = null;
            grainState.RecordExists = false;
            return ValueTask.CompletedTask;
        }

        private static string CreateStorageKey(string stateName, GrainId grainId)
            => $"{grainId.GrainType}/{grainId.Key}/{stateName}";

        private static TState CreateDefaultState<TState>()
        {
            if (typeof(TState) == typeof(string))
            {
                return (TState)(object)string.Empty;
            }

            return Activator.CreateInstance<TState>();
        }

        private sealed record StoredState(string Payload, string ETag);
    }

    private sealed class ExtensionCounterState
    {
        public int Total { get; set; }
    }
}
