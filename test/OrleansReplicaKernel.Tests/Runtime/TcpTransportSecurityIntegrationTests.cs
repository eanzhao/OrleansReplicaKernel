using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Demo;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Security;
using OrleansReplicaKernel.Serialization;
using OrleansReplicaKernel.Tests.TestSupport;

namespace OrleansReplicaKernel.Tests.Runtime;

public sealed class TcpTransportSecurityIntegrationTests
{
    [Fact]
    public async Task TcpTransport_WithTls_AllowsSiloToSiloInvocation()
    {
        var endpointMap = CreateEndpointMap();
        var sharedStateDirectory = CreateSharedStateDirectory();
        var membershipFile = Path.Combine(sharedStateDirectory, "membership.json");
        var grainDirectoryFile = Path.Combine(sharedStateDirectory, "grain-directory.json");
        var certificates = new Dictionary<string, X509Certificate2>(StringComparer.Ordinal)
        {
            ["dev-node-1"] = TestCertificateFactory.Create("dev-node-1"),
            ["dev-node-2"] = TestCertificateFactory.Create("dev-node-2")
        };

        try
        {
            await using var node2 = CreateSecureTcpBuilder(
                endpointMap,
                membershipFile,
                grainDirectoryFile,
                certificates,
                "echo:tls-echo=dev-node-2")
                .Build("dev-node-2", "dev-node-1");
            await using var node1 = CreateSecureTcpBuilder(
                endpointMap,
                membershipFile,
                grainDirectoryFile,
                certificates,
                "echo:tls-echo=dev-node-2")
                .Build("dev-node-1", "dev-node-2");

            await node1.WaitForAsync(
                () => ValueTask.FromResult(node1.GetHealthSnapshot().Nodes.Count),
                count => count == 2,
                TimeSpan.FromSeconds(5));

            var seeded = await node2.GetGrain<IEchoGrain>("tls-echo").PingAsync("seed");
            var result = await node1.GetGrain<IEchoGrain>("tls-echo").PingAsync("over-tls");

            Assert.Equal("echo:seed:count=1", seeded);
            Assert.Equal("echo:over-tls:count=2", result);
        }
        finally
        {
            DisposeCertificates(certificates.Values);
            DeleteSharedStateDirectory(sharedStateDirectory);
        }
    }

    [Fact]
    public async Task TcpTransport_WithTls_RejectsUnexpectedCertificateOnNodeJoin()
    {
        var endpointMap = CreateEndpointMap();
        var trustedCertificates = new Dictionary<string, X509Certificate2>(StringComparer.Ordinal)
        {
            ["dev-node-1"] = TestCertificateFactory.Create("dev-node-1"),
            ["dev-node-2"] = TestCertificateFactory.Create("dev-node-2")
        };
        var rogueCertificates = new Dictionary<string, X509Certificate2>(StringComparer.Ordinal)
        {
            ["dev-node-1"] = trustedCertificates["dev-node-1"],
            ["dev-node-2"] = TestCertificateFactory.Create("rogue-node-2")
        };
        var serializer = CreateBinaryMessageSerializer();
        var serverReceiver = new CountingRequestReceiver("dev-node-1");

        try
        {
            await using var node1Transport = new TcpMessageTransport(
                "dev-node-1",
                new IPEndPoint(IPAddress.Loopback, endpointMap["dev-node-1"]),
                new Dictionary<string, IPEndPoint>(StringComparer.Ordinal)
                {
                    ["dev-node-2"] = new IPEndPoint(IPAddress.Loopback, endpointMap["dev-node-2"])
                },
                new StaticClusterMembershipView("dev-node-1", ["dev-node-2"]),
                serializer,
                TimeSpan.FromMilliseconds(50),
                localSourceKind: InvocationSourceKind.ClusterNode,
                securityOptions: BuildSecurityOptions(
                    "dev-node-1",
                    trustedCertificates["dev-node-1"],
                    ("dev-node-2", InvocationSourceKind.ClusterNode, trustedCertificates["dev-node-2"])),
                acceptInboundConnections: true,
                allowUnknownInboundNodes: false);
            await using var rogueTransport = new TcpMessageTransport(
                "dev-node-2",
                new IPEndPoint(IPAddress.Loopback, endpointMap["dev-node-2"]),
                new Dictionary<string, IPEndPoint>(StringComparer.Ordinal)
                {
                    ["dev-node-1"] = new IPEndPoint(IPAddress.Loopback, endpointMap["dev-node-1"])
                },
                new StaticClusterMembershipView("dev-node-2", ["dev-node-1"]),
                serializer,
                TimeSpan.FromMilliseconds(50),
                localSourceKind: InvocationSourceKind.ClusterNode,
                securityOptions: BuildSecurityOptions(
                    "dev-node-2",
                    rogueCertificates["dev-node-2"],
                    ("dev-node-1", InvocationSourceKind.ClusterNode, trustedCertificates["dev-node-1"])),
                acceptInboundConnections: true,
                allowUnknownInboundNodes: false);

            node1Transport.Bind(serverReceiver, new NoOpResponseReceiver());
            rogueTransport.Bind(new NoOpRequestReceiver("dev-node-2"), new NoOpResponseReceiver());
            node1Transport.Start();
            rogueTransport.Start();

            var message = new InvocationMessage(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                AttemptSequence: 1,
                CreatedUtc: DateTimeOffset.UtcNow,
                SourceNodeName: "dev-node-2",
                Target: new GrainAddress("dev-node-1", new GrainId("echo", "tls-auth"), OwnerVersion: 1),
                Invokable: new EchoPingSlowInvokable("should-fail", 0),
                SourceKind: InvocationSourceKind.ClusterNode,
                Identity: InvocationIdentity.CreateAuthenticated(
                    "dev-node-2",
                    InvocationSourceKind.ClusterNode,
                    rogueCertificates["dev-node-2"]));

            var exception = await Record.ExceptionAsync(() => rogueTransport.SendAsync(message).AsTask());

            if (exception is not null)
            {
                Assert.True(
                    exception is AuthenticationException
                        or IOException
                        or ObjectDisposedException
                        or RemoteNodeUnavailableException,
                    $"Expected authentication-related failure but received {exception.GetType().Name}: {exception.Message}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
            Assert.Equal(0, serverReceiver.RequestCount);
        }
        finally
        {
            rogueCertificates["dev-node-2"].Dispose();
            trustedCertificates["dev-node-1"].Dispose();
            trustedCertificates["dev-node-2"].Dispose();
        }
    }

    [Fact]
    public async Task ClientIdentity_IsPropagatedThroughGatewayAndNestedGrainCalls_WhenTlsEnabled()
    {
        var endpointMap = CreateEndpointMap();
        var sharedStateDirectory = CreateSharedStateDirectory();
        var membershipFile = Path.Combine(sharedStateDirectory, "membership.json");
        var grainDirectoryFile = Path.Combine(sharedStateDirectory, "grain-directory.json");
        var certificates = new Dictionary<string, X509Certificate2>(StringComparer.Ordinal)
        {
            ["dev-node-1"] = TestCertificateFactory.Create("dev-node-1"),
            ["dev-node-2"] = TestCertificateFactory.Create("dev-node-2"),
            ["client-1"] = TestCertificateFactory.Create("client-1")
        };

        try
        {
            await using var node2 = CreateSecureTcpBuilder(
                endpointMap,
                membershipFile,
                grainDirectoryFile,
                certificates,
                "callerIdentity:root=dev-node-2")
                .Build("dev-node-2", "dev-node-1");
            await using var node1 = CreateSecureTcpBuilder(
                endpointMap,
                membershipFile,
                grainDirectoryFile,
                certificates,
                "callerIdentity:root=dev-node-2")
                .Build("dev-node-1", "dev-node-2");

            await node1.WaitForAsync(
                () => ValueTask.FromResult(node1.GetHealthSnapshot().Nodes.Count),
                count => count == 2,
                TimeSpan.FromSeconds(5));

            await using var client = CreateSecureTcpBuilder(
                endpointMap,
                membershipFile,
                grainDirectoryFile,
                certificates)
                .BuildClient("client-1", "dev-node-1");

            var expectedIdentity =
                $"Client|client-1|auth=True|thumbprint={TcpTransportSecurityOptions.NormalizeThumbprint(certificates["client-1"].Thumbprint)}";
            var root = client.GetGrain<ICallerIdentityGrain>("root");

            var currentIdentity = await root.GetCurrentIdentityAsync();
            var nestedIdentity = await root.GetNestedCurrentIdentityAsync("nested");

            Assert.Equal(expectedIdentity, currentIdentity);
            Assert.Equal(expectedIdentity, nestedIdentity);
        }
        finally
        {
            DisposeCertificates(certificates.Values);
            DeleteSharedStateDirectory(sharedStateDirectory);
        }
    }

    private static OrleansReplicaKernelBuilder CreateSecureTcpBuilder(
        IReadOnlyDictionary<string, int> endpointMap,
        string membershipFile,
        string grainDirectoryFile,
        IReadOnlyDictionary<string, X509Certificate2> certificates,
        params string[] seededOwners)
    {
        var builder = new OrleansReplicaKernelBuilder()
            .AddGeneratedGrainImplementationsFromAssembly(typeof(EchoGrain).Assembly)
            .AddGeneratedGrainReferencesFromAssembly(typeof(EchoGrainReference).Assembly)
            .UseTcpTransport()
            .UseTcpTls()
            .UseFileMembershipTable(membershipFile)
            .UseFileGrainDirectoryTable(grainDirectoryFile);

        foreach (var endpoint in endpointMap)
        {
            builder.WithTcpNodeEndpoint(endpoint.Key, new IPEndPoint(IPAddress.Loopback, endpoint.Value));
        }

        foreach (var certificate in certificates)
        {
            builder.WithTcpIdentityCertificate(certificate.Key, certificate.Value);
        }

        foreach (var seededOwner in seededOwners)
        {
            var splitIndex = seededOwner.IndexOf('=', StringComparison.Ordinal);
            var identity = seededOwner[..splitIndex];
            var ownerNodeName = seededOwner[(splitIndex + 1)..];
            var separatorIndex = identity.IndexOf(':', StringComparison.Ordinal);
            builder.SeedGrainOwner(
                identity[..separatorIndex],
                identity[(separatorIndex + 1)..],
                ownerNodeName);
        }

        return builder;
    }

    private static BinaryMessageSerializer CreateBinaryMessageSerializer()
        => new(new BinarySerializerBuilder()
            .AddCodecsFromAssembly(typeof(EchoGrain).Assembly)
            .Build());

    private static TcpTransportSecurityOptions BuildSecurityOptions(
        string localNodeName,
        X509Certificate2 localCertificate,
        params (string Name, InvocationSourceKind SourceKind, X509Certificate2 Certificate)[] trustedPeers)
        => new(
            localCertificate,
            trustedPeers.Select(peer => TcpTransportSecurityOptions.TrustedPeer.Create(
                peer.Name,
                peer.SourceKind,
                peer.Certificate)));

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

    private static void DisposeCertificates(IEnumerable<X509Certificate2> certificates)
    {
        foreach (var certificate in certificates.Distinct())
        {
            certificate.Dispose();
        }
    }

    private sealed class CountingRequestReceiver(string responderNodeName) : IMessageReceiver
    {
        public int RequestCount => Volatile.Read(ref _requestCount);

        private int _requestCount;

        public ValueTask<InvocationResponseMessage> ReceiveAsync(
            InvocationMessage message,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _requestCount);
            return ValueTask.FromResult(new InvocationResponseMessage(
                message.RequestId,
                message.AttemptId,
                message.AttemptSequence,
                responderNodeName,
                Result: "ok",
                Error: null));
        }
    }

    private sealed class NoOpRequestReceiver(string responderNodeName) : IMessageReceiver
    {
        public ValueTask<InvocationResponseMessage> ReceiveAsync(
            InvocationMessage message,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new InvocationResponseMessage(
                message.RequestId,
                message.AttemptId,
                message.AttemptSequence,
                responderNodeName,
                Result: null,
                Error: null));
    }

    private sealed class NoOpResponseReceiver : IResponseReceiver
    {
        public ValueTask ReceiveResponseAsync(
            InvocationResponseMessage response,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
