using System.Net;
using System.Net.Sockets;
using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Storage;
using OrleansReplicaKernel.Versioning;
using OrleansReplicaKernel.Serialization;

namespace OrleansReplicaKernel.Tests.Versioning;

public sealed class RollingUpgradeIntegrationTests
{
    private const string GrainType = "versioned-upgrade";

    [Fact]
    public async Task RollingUpgrade_RoutesVersionedCallsToCompatibleNodes_AndRelocatesOwner()
    {
        var endpointMap = CreateEndpointMap();
        var sharedStateDirectory = CreateSharedStateDirectory();
        var membershipFile = Path.Combine(sharedStateDirectory, "membership.json");
        var grainDirectoryFile = Path.Combine(sharedStateDirectory, "grain-directory.json");
        var interfaceVersionFile = Path.Combine(sharedStateDirectory, "grain-interface-versions.json");

        try
        {
            await using var oldNode = CreateOldBuilder(endpointMap, membershipFile, grainDirectoryFile, interfaceVersionFile)
                .SeedGrainOwner(GrainType, "upgrade", "dev-node-1")
                .Build("dev-node-1", "dev-node-2");
            await using var newNode = CreateNewBuilder(endpointMap, membershipFile, grainDirectoryFile, interfaceVersionFile)
                .Build("dev-node-2", "dev-node-1");

            await WaitForMembershipAsync(oldNode, expectedNodeCount: 2);
            await WaitForMembershipAsync(newNode, expectedNodeCount: 2);

            var beforeUpgrade = await oldNode.GetGrain<IVersionedUpgradeGrainV1>("upgrade").PingAsync("before-upgrade");
            var afterUpgrade = await newNode.GetGrain<IVersionedUpgradeGrainV2>("upgrade").PingAsync("v2", "after-upgrade");
            var oldContractAfterRelocation = await oldNode.GetGrain<IVersionedUpgradeGrainV1>("upgrade").PingAsync("after-relocation");

            Assert.Equal("v1:before-upgrade:count=1", beforeUpgrade);
            Assert.Equal("v2:after-upgrade:count=1", afterUpgrade);
            Assert.Equal("v1:after-relocation:count=2", oldContractAfterRelocation);
            Assert.Contains($"{GrainType}/upgrade->dev-node-2@v2", oldNode.DescribeGrainDirectory(), StringComparison.Ordinal);
            Assert.Contains($"{GrainType}/upgrade->dev-node-2@v2", newNode.DescribeGrainDirectory(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteSharedStateDirectory(sharedStateDirectory);
        }
    }

    [Fact]
    public async Task RollingUpgrade_NewerStateReader_CanReadStateWrittenByOlderVersion()
    {
        var storageDirectory = CreateSharedStateDirectory();
        var storagePath = Path.Combine(storageDirectory, "state.json");

        try
        {
            var storage = new FileGrainStorage(storagePath);
            var grainId = new GrainId(GrainType, "state-upgrade");

            var writer = new PersistentState<UpgradeStateV1>("upgrade", grainId, storage);
            writer.State = new UpgradeStateV1
            {
                Message = "written-by-v1"
            };
            await writer.WriteStateAsync();

            var reader = new PersistentState<UpgradeStateV2>("upgrade", grainId, storage);
            await reader.ReadStateAsync();

            Assert.True(reader.RecordExists);
            Assert.Equal("written-by-v1", reader.State.Message);
            Assert.Equal(0, reader.State.Revision);
        }
        finally
        {
            DeleteSharedStateDirectory(storageDirectory);
        }
    }

    private static OrleansReplicaKernelBuilder CreateOldBuilder(
        IReadOnlyDictionary<string, int> endpointMap,
        string membershipFile,
        string grainDirectoryFile,
        string interfaceVersionFile)
        => CreateBaseBuilder(endpointMap, membershipFile, grainDirectoryFile, interfaceVersionFile)
            .AddBinaryCodec(new VersionedUpgradePingV1InvokableCodec())
            .AddGrain<IVersionedUpgradeGrainV1, VersionedUpgradeGrain>(
                GrainType,
                static () => new VersionedUpgradeGrain(),
                static (runtime, grainId) => new VersionedUpgradeGrainV1Reference(runtime, grainId));

    private static OrleansReplicaKernelBuilder CreateNewBuilder(
        IReadOnlyDictionary<string, int> endpointMap,
        string membershipFile,
        string grainDirectoryFile,
        string interfaceVersionFile)
        => CreateBaseBuilder(endpointMap, membershipFile, grainDirectoryFile, interfaceVersionFile)
            .AddBinaryCodec(new VersionedUpgradePingV1InvokableCodec())
            .AddBinaryCodec(new VersionedUpgradePingV2InvokableCodec())
            .AddGrain<IVersionedUpgradeGrainV1, VersionedUpgradeGrain>(
                GrainType,
                static () => new VersionedUpgradeGrain(),
                static (runtime, grainId) => new VersionedUpgradeGrainV1Reference(runtime, grainId))
            .AddGrainReference<IVersionedUpgradeGrainV2>(
                GrainType,
                static (runtime, grainId) => new VersionedUpgradeGrainV2Reference(runtime, grainId));

    private static OrleansReplicaKernelBuilder CreateBaseBuilder(
        IReadOnlyDictionary<string, int> endpointMap,
        string membershipFile,
        string grainDirectoryFile,
        string interfaceVersionFile)
        => new OrleansReplicaKernelBuilder()
            .UseTcpTransport()
            .UseFileMembershipTable(membershipFile)
            .UseFileGrainDirectoryTable(grainDirectoryFile)
            .UseFileGrainInterfaceVersionTable(interfaceVersionFile)
            .WithTcpNodeEndpoint("dev-node-1", new IPEndPoint(IPAddress.Loopback, endpointMap["dev-node-1"]))
            .WithTcpNodeEndpoint("dev-node-2", new IPEndPoint(IPAddress.Loopback, endpointMap["dev-node-2"]));

    private static async Task WaitForMembershipAsync(OrleansReplicaKernelHost host, int expectedNodeCount)
    {
        await host.WaitForAsync(
            () => ValueTask.FromResult(host.GetHealthSnapshot().Nodes.Count),
            count => count == expectedNodeCount,
            TimeSpan.FromSeconds(5));
    }

    private static Dictionary<string, int> CreateEndpointMap()
        => new(StringComparer.Ordinal)
        {
            ["dev-node-1"] = GetFreeTcpPort(),
            ["dev-node-2"] = GetFreeTcpPort()
        };

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string CreateSharedStateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "orleans-replica-kernel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteSharedStateDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [GrainInterfaceVersion("test.versioned-upgrade", 1)]
    private interface IVersionedUpgradeGrainV1
    {
        Task<string> PingAsync(string text, CancellationToken cancellationToken = default);
    }

    [GrainInterfaceVersion("test.versioned-upgrade", 2)]
    private interface IVersionedUpgradeGrainV2
    {
        Task<string> PingAsync(string prefix, string text, CancellationToken cancellationToken = default);
    }

    private sealed class VersionedUpgradeGrain : IVersionedUpgradeGrainV1, IVersionedUpgradeGrainV2
    {
        private int _callCount;

        public Task<string> PingAsync(string text, CancellationToken cancellationToken = default)
        {
            _callCount++;
            return Task.FromResult($"v1:{text}:count={_callCount}");
        }

        public Task<string> PingAsync(string prefix, string text, CancellationToken cancellationToken = default)
        {
            _callCount++;
            return Task.FromResult($"{prefix}:{text}:count={_callCount}");
        }
    }

    private sealed class VersionedUpgradeGrainV1Reference : IVersionedUpgradeGrainV1
    {
        private readonly IInvocationRuntime _runtime;
        private readonly GrainId _grainId;

        public VersionedUpgradeGrainV1Reference(IInvocationRuntime runtime, GrainId grainId)
        {
            _runtime = runtime;
            _grainId = grainId;
        }

        public Task<string> PingAsync(string text, CancellationToken cancellationToken = default)
            => _runtime.InvokeAsync<string>(_grainId, new VersionedUpgradePingV1Invokable(text), cancellationToken).AsTask();
    }

    private sealed class VersionedUpgradeGrainV2Reference : IVersionedUpgradeGrainV2
    {
        private readonly IInvocationRuntime _runtime;
        private readonly GrainId _grainId;

        public VersionedUpgradeGrainV2Reference(IInvocationRuntime runtime, GrainId grainId)
        {
            _runtime = runtime;
            _grainId = grainId;
        }

        public Task<string> PingAsync(string prefix, string text, CancellationToken cancellationToken = default)
            => _runtime.InvokeAsync<string>(_grainId, new VersionedUpgradePingV2Invokable(prefix, text), cancellationToken).AsTask();
    }

    private sealed class VersionedUpgradePingV1Invokable : IInvokable
    {
        public VersionedUpgradePingV1Invokable(string text)
        {
            Text = text;
        }

        public string Text { get; }

        public string InterfaceName => nameof(IVersionedUpgradeGrainV1);

        public string InterfaceCompatibilityFamily => "test.versioned-upgrade";

        public int InterfaceVersion => 1;

        public string MethodName => nameof(IVersionedUpgradeGrainV1.PingAsync);

        public async ValueTask<object?> InvokeAsync(object target, CancellationToken cancellationToken)
            => await ((IVersionedUpgradeGrainV1)target).PingAsync(Text, cancellationToken);
    }

    private sealed class VersionedUpgradePingV2Invokable : IInvokable
    {
        public VersionedUpgradePingV2Invokable(string prefix, string text)
        {
            Prefix = prefix;
            Text = text;
        }

        public string Prefix { get; }

        public string Text { get; }

        public string InterfaceName => nameof(IVersionedUpgradeGrainV2);

        public string InterfaceCompatibilityFamily => "test.versioned-upgrade";

        public int InterfaceVersion => 2;

        public string MethodName => nameof(IVersionedUpgradeGrainV2.PingAsync);

        public async ValueTask<object?> InvokeAsync(object target, CancellationToken cancellationToken)
            => await ((IVersionedUpgradeGrainV2)target).PingAsync(Prefix, Text, cancellationToken);
    }

    private sealed class VersionedUpgradePingV1InvokableCodec : BinaryObjectCodec<VersionedUpgradePingV1Invokable>
    {
        public override string Alias => "test.versioned-upgrade.invokable.v1.ping";

        protected override VersionedUpgradePingV1Invokable ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
        {
            string text = string.Empty;

            while (reader.TryReadField(out var fieldId, out var payload))
            {
                if (fieldId == 1)
                {
                    text = serializer.Read<string>(payload);
                }
            }

            return new VersionedUpgradePingV1Invokable(text);
        }

        protected override void WriteFields(
            BinaryObjectWriter writer,
            VersionedUpgradePingV1Invokable value,
            BinarySerializer serializer)
        {
            writer.WriteField(1, value.Text, serializer);
        }
    }

    private sealed class VersionedUpgradePingV2InvokableCodec : BinaryObjectCodec<VersionedUpgradePingV2Invokable>
    {
        public override string Alias => "test.versioned-upgrade.invokable.v2.ping";

        protected override VersionedUpgradePingV2Invokable ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
        {
            string prefix = string.Empty;
            string text = string.Empty;

            while (reader.TryReadField(out var fieldId, out var payload))
            {
                switch (fieldId)
                {
                    case 1:
                        prefix = serializer.Read<string>(payload);
                        break;
                    case 2:
                        text = serializer.Read<string>(payload);
                        break;
                }
            }

            return new VersionedUpgradePingV2Invokable(prefix, text);
        }

        protected override void WriteFields(
            BinaryObjectWriter writer,
            VersionedUpgradePingV2Invokable value,
            BinarySerializer serializer)
        {
            writer.WriteField(1, value.Prefix, serializer);
            writer.WriteField(2, value.Text, serializer);
        }
    }

    private sealed class UpgradeStateV1
    {
        public string Message { get; set; } = string.Empty;
    }

    private sealed class UpgradeStateV2
    {
        public string Message { get; set; } = string.Empty;

        public int Revision { get; set; }
    }
}
