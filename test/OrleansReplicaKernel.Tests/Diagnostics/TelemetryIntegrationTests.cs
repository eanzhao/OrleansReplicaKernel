using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Diagnostics;
using OrleansReplicaKernel.Demo;

namespace OrleansReplicaKernel.Tests.Diagnostics;

public sealed class TelemetryIntegrationTests
{
    [Fact]
    public async Task Telemetry_RecordsCoreMetrics_OnGrainInvocation()
    {
        var measurements = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        using var meterListener = CreateMeterListener(measurements);

        await using var host = new OrleansReplicaKernelBuilder()
            .AddGeneratedGrainImplementationsFromAssembly(typeof(EchoGrain).Assembly)
            .AddGeneratedGrainReferencesFromAssembly(typeof(EchoGrainReference).Assembly)
            .Build("dev-node-1", "dev-node-2");

        var result = await host.GetGrain<IEchoGrain>("metrics").PingAsync("metrics");

        Assert.Equal("echo:metrics:count=1", result);
        Assert.Contains("orleans-replica-kernel.activation.count", measurements.Keys);
        Assert.Contains("orleans-replica-kernel.turn.duration", measurements.Keys);
        Assert.Contains("orleans-replica-kernel.message.latency", measurements.Keys);
        Assert.Contains("orleans-replica-kernel.membership.changes", measurements.Keys);
    }

    [Fact]
    public async Task Telemetry_PropagatesTraceAcrossClientGatewayAndOwner()
    {
        var endpointMap = CreateEndpointMap();
        var sharedStateDirectory = CreateSharedStateDirectory();
        var membershipFile = Path.Combine(sharedStateDirectory, "membership.json");
        var grainDirectoryFile = Path.Combine(sharedStateDirectory, "grain-directory.json");

        try
        {
            using var activityCapture = new ActivityCapture();

            await using var node2 = CreateTcpBuilder(endpointMap, membershipFile, grainDirectoryFile)
                .Build("dev-node-2", "dev-node-1");
            await using var node1 = CreateTcpBuilder(endpointMap, membershipFile, grainDirectoryFile)
                .Build("dev-node-1", "dev-node-2");

            await node1.WaitForAsync(
                () => ValueTask.FromResult(node1.GetHealthSnapshot().Nodes.Count),
                count => count == 2,
                TimeSpan.FromSeconds(5));

            await using var client = CreateTcpBuilder(endpointMap, membershipFile, grainDirectoryFile)
                .BuildClient("client-1", "dev-node-1");

            var result = await client.GetGrain<IEchoGrain>("telemetry").PingAsync("trace");

            Assert.Equal("echo:trace:count=1", result);

            var clientSpan = Assert.Single(activityCapture.Completed, item =>
                item.OperationName == "orleans.invoke"
                && string.Equals(item.NodeName, "client-1", StringComparison.Ordinal));
            var tracedSpans = activityCapture.Completed
                .Where(item => string.Equals(item.TraceId, clientSpan.TraceId, StringComparison.Ordinal))
                .ToArray();
            var gatewaySpan = Assert.Single(tracedSpans, item =>
                item.OperationName == "orleans.receive"
                && string.Equals(item.NodeName, "dev-node-1", StringComparison.Ordinal));
            var ownerSpan = Assert.Single(tracedSpans, item =>
                item.OperationName == "orleans.receive"
                && string.Equals(item.NodeName, "dev-node-2", StringComparison.Ordinal));

            Assert.Equal(clientSpan.TraceId, gatewaySpan.TraceId);
            Assert.Equal(clientSpan.TraceId, ownerSpan.TraceId);
            Assert.Equal(clientSpan.SpanId, gatewaySpan.ParentSpanId);
            Assert.Equal(gatewaySpan.SpanId, ownerSpan.ParentSpanId);
        }
        finally
        {
            DeleteSharedStateDirectory(sharedStateDirectory);
        }
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsCurrentHostSnapshot()
    {
        var port = GetFreeTcpPort();
        var prefix = $"http://127.0.0.1:{port}/";

        await using var host = new OrleansReplicaKernelBuilder()
            .AddGeneratedGrainImplementationsFromAssembly(typeof(EchoGrain).Assembly)
            .AddGeneratedGrainReferencesFromAssembly(typeof(EchoGrainReference).Assembly)
            .UseHealthCheckEndpoint(prefix)
            .Build("dev-node-1");

        var result = await host.GetGrain<IEchoGrain>("health").PingAsync("health");
        Assert.Equal("echo:health:count=1", result);

        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync(prefix + "healthz");
        var payload = await response.Content.ReadAsStringAsync();
        var snapshot = JsonSerializer.Deserialize<KernelHealthSnapshot>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(response.IsSuccessStatusCode);
        Assert.NotNull(snapshot);
        Assert.Equal("dev-node-1", snapshot!.PrimaryNodeName);

        var node = Assert.Single(snapshot.Nodes);
        Assert.Equal("dev-node-1", node.NodeName);
        Assert.Equal(1, node.ActivationCount);
    }

    private static OrleansReplicaKernelBuilder CreateTcpBuilder(
        IReadOnlyDictionary<string, int> endpointMap,
        string membershipFile,
        string grainDirectoryFile)
        => new OrleansReplicaKernelBuilder()
            .AddGeneratedGrainImplementationsFromAssembly(typeof(EchoGrain).Assembly)
            .AddGeneratedGrainReferencesFromAssembly(typeof(EchoGrainReference).Assembly)
            .UseTcpTransport()
            .UseFileMembershipTable(membershipFile)
            .UseFileGrainDirectoryTable(grainDirectoryFile)
            .SeedGrainOwner("echo", "telemetry", "dev-node-2")
            .WithTcpNodeEndpoint("dev-node-1", new IPEndPoint(IPAddress.Loopback, endpointMap["dev-node-1"]))
            .WithTcpNodeEndpoint("dev-node-2", new IPEndPoint(IPAddress.Loopback, endpointMap["dev-node-2"]));

    private static MeterListener CreateMeterListener(ConcurrentDictionary<string, int> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (string.Equals(instrument.Meter.Name, OrleansReplicaKernelTelemetry.MeterName, StringComparison.Ordinal))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            measurements.AddOrUpdate(instrument.Name, 1, static (_, current) => current + 1));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
            measurements.AddOrUpdate(instrument.Name, 1, static (_, current) => current + 1));
        listener.Start();
        return listener;
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

    private sealed class ActivityCapture : IDisposable
    {
        private int _disposed;
        private readonly ActivityListener _listener;

        public ActivityCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source =>
                    string.Equals(source.Name, OrleansReplicaKernelTelemetry.ActivitySourceName, StringComparison.Ordinal),
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        return;
                    }

                    Completed.Enqueue(new ActivitySnapshot(
                        activity.OperationName,
                        activity.TraceId.ToString(),
                        activity.SpanId.ToString(),
                        activity.ParentSpanId.ToString(),
                        activity.Kind,
                        activity.TagObjects.FirstOrDefault(item =>
                            string.Equals(item.Key, "orleans.node.name", StringComparison.Ordinal)).Value?.ToString() ?? string.Empty));
                }
            };

            ActivitySource.AddActivityListener(_listener);
        }

        public ConcurrentQueue<ActivitySnapshot> Completed { get; } = new();

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            _listener.Dispose();
        }
    }

    private sealed record ActivitySnapshot(
        string OperationName,
        string TraceId,
        string SpanId,
        string ParentSpanId,
        ActivityKind Kind,
        string NodeName);
}
