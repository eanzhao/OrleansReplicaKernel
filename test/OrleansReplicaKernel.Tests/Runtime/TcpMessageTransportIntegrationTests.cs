using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Tests.Runtime;

public sealed class TcpMessageTransportIntegrationTests
{
    private const string ControlPrefix = "@@worker@@";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TcpTransport_CanInvokeRemoteGrain_AcrossIndependentProcesses()
    {
        var endpointMap = CreateEndpointMap();
        await using var node2 = await WorkerProcess.StartAsync("dev-node-2", endpointMap, "echo:remote=dev-node-2");
        await using var node1 = await WorkerProcess.StartAsync("dev-node-1", endpointMap, "echo:remote=dev-node-2");

        var result = await node1.PingAsync("remote", "cross-process");

        Assert.Equal("echo:cross-process:count=1", result);
    }

    [Fact]
    public async Task TcpTransport_Reconnects_AfterRemoteProcessRestarts()
    {
        var endpointMap = CreateEndpointMap();
        await using var node2 = await WorkerProcess.StartAsync("dev-node-2", endpointMap, "echo:remote=dev-node-2");
        await using var node1 = await WorkerProcess.StartAsync("dev-node-1", endpointMap, "echo:remote=dev-node-2");

        var first = await node1.PingAsync("remote", "before-restart");
        Assert.Equal("echo:before-restart:count=1", first);

        await node2.DisposeAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await using var restartedNode2 = await WorkerProcess.StartAsync("dev-node-2", endpointMap, "echo:remote=dev-node-2");
        var second = await node1.PingAsync("remote", "after-restart");

        Assert.Equal("echo:after-restart:count=1", second);
    }

    [Fact]
    public async Task TcpTransport_SharedMembershipTable_AllowsDiscoveryAndProbeDrivenHealthUpdates()
    {
        var endpointMap = CreateEndpointMap();
        var membershipDirectory = Path.Combine(Path.GetTempPath(), "orleans-replica-kernel-tests", Guid.NewGuid().ToString("N"));
        var membershipFile = Path.Combine(membershipDirectory, "membership.json");
        Directory.CreateDirectory(membershipDirectory);

        WorkerProcess? node2 = null;
        WorkerProcess? node1 = null;
        try
        {
            node2 = await WorkerProcess.StartWithMembershipAsync("dev-node-2", endpointMap, membershipFile);
            node1 = await WorkerProcess.StartWithMembershipAsync("dev-node-1", endpointMap, membershipFile);

            await WaitForMembershipAsync(node1, "dev-node-1", "dev-node-2");
            await WaitForMembershipAsync(node2, "dev-node-1", "dev-node-2");

            var beforeFailure = await node1.GetMembershipAsync();
            Assert.Contains(
                beforeFailure,
                item => item.NodeName == "dev-node-2" && item.HealthStatus == nameof(NodeHealthStatus.Healthy));

            await node2.DisposeAsync();
            node2 = null;

            var firstProbeCount = await node1.RunProbeAsync();
            Assert.Equal(1, firstProbeCount);
            await WaitForHealthAsync(node1, "dev-node-2", nameof(NodeHealthStatus.Suspect));

            var secondProbeCount = await node1.RunProbeAsync();
            Assert.Equal(1, secondProbeCount);
            await WaitForHealthAsync(node1, "dev-node-2", nameof(NodeHealthStatus.Unhealthy));
        }
        finally
        {
            if (node1 is not null)
            {
                await node1.DisposeAsync();
            }

            if (node2 is not null)
            {
                await node2.DisposeAsync();
            }

            if (Directory.Exists(membershipDirectory))
            {
                Directory.Delete(membershipDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TcpTransport_SharedGrainDirectoryTable_AllowsCrossProcessOwnerLookupWithoutSeeding()
    {
        var endpointMap = CreateEndpointMap();
        var sharedStateDirectory = CreateSharedStateDirectory();
        var membershipFile = Path.Combine(sharedStateDirectory, "membership.json");
        var grainDirectoryFile = Path.Combine(sharedStateDirectory, "grain-directory.json");

        WorkerProcess? node2 = null;
        WorkerProcess? node1 = null;
        try
        {
            node2 = await WorkerProcess.StartWithSharedTablesAsync("dev-node-2", endpointMap, membershipFile, grainDirectoryFile);
            node1 = await WorkerProcess.StartWithSharedTablesAsync("dev-node-1", endpointMap, membershipFile, grainDirectoryFile);

            await WaitForMembershipAsync(node1, "dev-node-1", "dev-node-2");
            await WaitForMembershipAsync(node2, "dev-node-1", "dev-node-2");

            var first = await node1.PingAsync("shared-directory", "first-hop");
            var node1Directory = await node1.GetDirectoryAsync();
            var node2Directory = await node2.GetDirectoryAsync();
            var second = await node2.PingAsync("shared-directory", "second-hop");

            Assert.Equal("echo:first-hop:count=1", first);
            Assert.Equal("echo/shared-directory->dev-node-1@v1", node1Directory);
            Assert.Equal("echo/shared-directory->dev-node-1@v1", node2Directory);
            Assert.Equal("echo:second-hop:count=2", second);
        }
        finally
        {
            if (node1 is not null)
            {
                await node1.DisposeAsync();
            }

            if (node2 is not null)
            {
                await node2.DisposeAsync();
            }

            DeleteSharedStateDirectory(sharedStateDirectory);
        }
    }

    [Fact]
    public async Task TcpTransport_SharedGrainDirectoryTable_RelocatesOwnerAfterFailedNodeBecomesUnhealthy()
    {
        var endpointMap = CreateEndpointMap();
        var sharedStateDirectory = CreateSharedStateDirectory();
        var membershipFile = Path.Combine(sharedStateDirectory, "membership.json");
        var grainDirectoryFile = Path.Combine(sharedStateDirectory, "grain-directory.json");

        WorkerProcess? node2 = null;
        WorkerProcess? node1 = null;
        try
        {
            node2 = await WorkerProcess.StartWithSharedTablesAsync("dev-node-2", endpointMap, membershipFile, grainDirectoryFile);
            node1 = await WorkerProcess.StartWithSharedTablesAsync("dev-node-1", endpointMap, membershipFile, grainDirectoryFile);

            await WaitForMembershipAsync(node1, "dev-node-1", "dev-node-2");
            await WaitForMembershipAsync(node2, "dev-node-1", "dev-node-2");

            var seeded = await node2.PingAsync("directory-failover", "seed-on-node2");
            var routed = await node1.PingAsync("directory-failover", "route-to-node2");

            Assert.Equal("echo:seed-on-node2:count=1", seeded);
            Assert.Equal("echo:route-to-node2:count=2", routed);

            await node2.DisposeAsync();
            node2 = null;

            Assert.Equal(1, await node1.RunProbeAsync());
            await WaitForHealthAsync(node1, "dev-node-2", nameof(NodeHealthStatus.Suspect));

            Assert.Equal(1, await node1.RunProbeAsync());
            await WaitForHealthAsync(node1, "dev-node-2", nameof(NodeHealthStatus.Unhealthy));

            var afterFailover = await node1.PingAsync("directory-failover", "after-failover");

            Assert.Equal("echo:after-failover:count=1", afterFailover);
        }
        finally
        {
            if (node1 is not null)
            {
                await node1.DisposeAsync();
            }

            if (node2 is not null)
            {
                await node2.DisposeAsync();
            }

            DeleteSharedStateDirectory(sharedStateDirectory);
        }
    }

    [Fact]
    public async Task TcpTransport_SharedGrainDirectoryTable_RoutesAcrossThreeNodeCluster()
    {
        var endpointMap = CreateEndpointMap("dev-node-1", "dev-node-2", "dev-node-3");
        var sharedStateDirectory = CreateSharedStateDirectory();
        var membershipFile = Path.Combine(sharedStateDirectory, "membership.json");
        var grainDirectoryFile = Path.Combine(sharedStateDirectory, "grain-directory.json");

        WorkerProcess? node3 = null;
        WorkerProcess? node2 = null;
        WorkerProcess? node1 = null;
        try
        {
            node3 = await WorkerProcess.StartWithSharedTablesAsync("dev-node-3", endpointMap, membershipFile, grainDirectoryFile);
            node2 = await WorkerProcess.StartWithSharedTablesAsync("dev-node-2", endpointMap, membershipFile, grainDirectoryFile);
            node1 = await WorkerProcess.StartWithSharedTablesAsync("dev-node-1", endpointMap, membershipFile, grainDirectoryFile);

            await WaitForMembershipAsync(node1, "dev-node-1", "dev-node-2", "dev-node-3");
            await WaitForMembershipAsync(node2, "dev-node-1", "dev-node-2", "dev-node-3");
            await WaitForMembershipAsync(node3, "dev-node-1", "dev-node-2", "dev-node-3");

            var seeded = await node3.PingAsync("three-node-directory", "seed-on-node3");
            var viaNode1 = await node1.PingAsync("three-node-directory", "route-from-node1");
            var viaNode2 = await node2.PingAsync("three-node-directory", "route-from-node2");

            Assert.Equal("echo:seed-on-node3:count=1", seeded);
            Assert.Equal("echo:route-from-node1:count=2", viaNode1);
            Assert.Equal("echo:route-from-node2:count=3", viaNode2);
            Assert.Equal("echo/three-node-directory->dev-node-3@v1", await node1.GetDirectoryAsync());
            Assert.Equal("echo/three-node-directory->dev-node-3@v1", await node2.GetDirectoryAsync());
            Assert.Equal("echo/three-node-directory->dev-node-3@v1", await node3.GetDirectoryAsync());
        }
        finally
        {
            if (node1 is not null)
            {
                await node1.DisposeAsync();
            }

            if (node2 is not null)
            {
                await node2.DisposeAsync();
            }

            if (node3 is not null)
            {
                await node3.DisposeAsync();
            }

            DeleteSharedStateDirectory(sharedStateDirectory);
        }
    }

    [Fact]
    public async Task TcpTransport_SharedGrainDirectoryTable_ReactivatesOnHealthyNodeAfterOwnerFailureInThreeNodeCluster()
    {
        var endpointMap = CreateEndpointMap("dev-node-1", "dev-node-2", "dev-node-3");
        var sharedStateDirectory = CreateSharedStateDirectory();
        var membershipFile = Path.Combine(sharedStateDirectory, "membership.json");
        var grainDirectoryFile = Path.Combine(sharedStateDirectory, "grain-directory.json");

        WorkerProcess? node3 = null;
        WorkerProcess? node2 = null;
        WorkerProcess? node1 = null;
        try
        {
            node3 = await WorkerProcess.StartWithSharedTablesAsync("dev-node-3", endpointMap, membershipFile, grainDirectoryFile);
            node2 = await WorkerProcess.StartWithSharedTablesAsync("dev-node-2", endpointMap, membershipFile, grainDirectoryFile);
            node1 = await WorkerProcess.StartWithSharedTablesAsync("dev-node-1", endpointMap, membershipFile, grainDirectoryFile);

            await WaitForMembershipAsync(node1, "dev-node-1", "dev-node-2", "dev-node-3");
            await WaitForMembershipAsync(node2, "dev-node-1", "dev-node-2", "dev-node-3");
            await WaitForMembershipAsync(node3, "dev-node-1", "dev-node-2", "dev-node-3");

            var seeded = await node3.PingAsync("three-node-failover", "seed-on-node3");
            var routed = await node1.PingAsync("three-node-failover", "route-to-node3");

            Assert.Equal("echo:seed-on-node3:count=1", seeded);
            Assert.Equal("echo:route-to-node3:count=2", routed);

            await node3.DisposeAsync();
            node3 = null;

            Assert.Equal(2, await node1.RunProbeAsync());
            await WaitForHealthAsync(node1, "dev-node-3", nameof(NodeHealthStatus.Suspect));

            Assert.Equal(2, await node1.RunProbeAsync());
            await WaitForHealthAsync(node1, "dev-node-3", nameof(NodeHealthStatus.Unhealthy));

            var afterFailover = await node1.PingAsync("three-node-failover", "after-failover");
            var directoryAfterFailover = await node1.GetDirectoryAsync();

            Assert.Equal("echo:after-failover:count=1", afterFailover);
            Assert.DoesNotContain("echo/three-node-failover->dev-node-3", directoryAfterFailover, StringComparison.Ordinal);
        }
        finally
        {
            if (node1 is not null)
            {
                await node1.DisposeAsync();
            }

            if (node2 is not null)
            {
                await node2.DisposeAsync();
            }

            if (node3 is not null)
            {
                await node3.DisposeAsync();
            }

            DeleteSharedStateDirectory(sharedStateDirectory);
        }
    }

    private static Dictionary<string, int> CreateEndpointMap(params string[] nodeNames)
    {
        var names = nodeNames.Length == 0
            ? ["dev-node-1", "dev-node-2"]
            : nodeNames;

        return names.ToDictionary(nodeName => nodeName, _ => GetFreeTcpPort(), StringComparer.Ordinal);
    }

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

    private static async Task WaitForMembershipAsync(WorkerProcess worker, params string[] expectedNodeNames)
    {
        var timeoutAt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < timeoutAt)
        {
            var membership = await worker.GetMembershipAsync();
            var actualNodeNames = membership
                .Select(item => item.NodeName)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            var expected = expectedNodeNames
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();

            if (actualNodeNames.SequenceEqual(expected, StringComparer.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException($"Timed out waiting for membership {string.Join(", ", expectedNodeNames)}.");
    }

    private static async Task WaitForHealthAsync(WorkerProcess worker, string nodeName, string expectedHealthStatus)
    {
        var timeoutAt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < timeoutAt)
        {
            var membership = await worker.GetMembershipAsync();
            var member = membership.FirstOrDefault(item => item.NodeName == nodeName);
            if (member is not null && member.HealthStatus == expectedHealthStatus)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException($"Timed out waiting for {nodeName} -> {expectedHealthStatus}.");
    }

    private sealed class WorkerProcess : IAsyncDisposable
    {
        private int _disposed;
        private readonly Process _process;
        private readonly StringBuilder _output = new();
        private readonly SemaphoreSlim _commandLock = new(1, 1);

        private WorkerProcess(Process process)
        {
            _process = process;
        }

        public static Task<WorkerProcess> StartAsync(
            string nodeName,
            IReadOnlyDictionary<string, int> endpoints,
            params string[] seededOwners)
            => StartCoreAsync(nodeName, endpoints, null, null, seededOwners);

        public static Task<WorkerProcess> StartWithMembershipAsync(
            string nodeName,
            IReadOnlyDictionary<string, int> endpoints,
            string membershipFile,
            params string[] seededOwners)
            => StartCoreAsync(nodeName, endpoints, membershipFile, null, seededOwners);

        public static Task<WorkerProcess> StartWithSharedTablesAsync(
            string nodeName,
            IReadOnlyDictionary<string, int> endpoints,
            string membershipFile,
            string grainDirectoryFile,
            params string[] seededOwners)
            => StartCoreAsync(nodeName, endpoints, membershipFile, grainDirectoryFile, seededOwners);

        private static async Task<WorkerProcess> StartCoreAsync(
            string nodeName,
            IReadOnlyDictionary<string, int> endpoints,
            string? membershipFile,
            string? grainDirectoryFile,
            string[] seededOwners)
        {
            var workerAssemblyPath = typeof(NetworkWorkerAnchor).Assembly.Location;
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add(workerAssemblyPath);
            startInfo.ArgumentList.Add("--node-name");
            startInfo.ArgumentList.Add(nodeName);
            startInfo.ArgumentList.Add("--heartbeat-ms");
            startInfo.ArgumentList.Add("50");

            if (!string.IsNullOrWhiteSpace(membershipFile))
            {
                startInfo.ArgumentList.Add("--membership-file");
                startInfo.ArgumentList.Add(membershipFile);
            }

            if (!string.IsNullOrWhiteSpace(grainDirectoryFile))
            {
                startInfo.ArgumentList.Add("--directory-file");
                startInfo.ArgumentList.Add(grainDirectoryFile);
            }

            foreach (var endpoint in endpoints)
            {
                startInfo.ArgumentList.Add("--endpoint");
                startInfo.ArgumentList.Add($"{endpoint.Key}={endpoint.Value}");
            }

            foreach (var seededOwner in seededOwners)
            {
                startInfo.ArgumentList.Add("--seed-owner");
                startInfo.ArgumentList.Add(seededOwner);
            }

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start worker '{nodeName}'.");
            var worker = new WorkerProcess(process);

            var ready = await worker.ReadControlResponseAsync();
            if (!string.Equals(ready.Type, "ready", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Worker '{nodeName}' failed to report ready state. Output: {worker._output}");
            }

            return worker;
        }

        public async Task<string> PingAsync(string key, string text)
        {
            await _commandLock.WaitAsync();
            try
            {
                await WriteCommandAsync(new WorkerCommand("ping", key, text));
                var response = await ReadControlResponseAsync();
                return response.Type switch
                {
                    "result" => response.Result ?? throw new InvalidOperationException("Worker returned an empty ping result."),
                    "error" => throw new InvalidOperationException($"Worker ping failed: {response.Error}\n{_output}"),
                    _ => throw new InvalidOperationException($"Unexpected worker response '{response.Type}'.\n{_output}")
                };
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<IReadOnlyList<WorkerMembershipRecord>> GetMembershipAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await WriteCommandAsync(new WorkerCommand("membership", null, null));
                var response = await ReadControlResponseAsync();
                return response.Type switch
                {
                    "membership" => JsonSerializer.Deserialize<WorkerMembershipRecord[]>(
                            response.Result ?? throw new InvalidOperationException("Worker returned an empty membership payload."),
                            JsonOptions)
                        ?? throw new InvalidOperationException("Worker returned invalid membership payload."),
                    "error" => throw new InvalidOperationException($"Worker membership query failed: {response.Error}\n{_output}"),
                    _ => throw new InvalidOperationException($"Unexpected worker response '{response.Type}'.\n{_output}")
                };
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<string> GetDirectoryAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await WriteCommandAsync(new WorkerCommand("directory", null, null));
                var response = await ReadControlResponseAsync();
                return response.Type switch
                {
                    "directory" => response.Result ?? throw new InvalidOperationException("Worker returned an empty directory payload."),
                    "error" => throw new InvalidOperationException($"Worker directory query failed: {response.Error}\n{_output}"),
                    _ => throw new InvalidOperationException($"Unexpected worker response '{response.Type}'.\n{_output}")
                };
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<int> RunProbeAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await WriteCommandAsync(new WorkerCommand("probe", null, null));
                var response = await ReadControlResponseAsync();
                return response.Type switch
                {
                    "probe" => int.Parse(response.Result ?? throw new InvalidOperationException("Worker returned an empty probe result.")),
                    "error" => throw new InvalidOperationException($"Worker probe failed: {response.Error}\n{_output}"),
                    _ => throw new InvalidOperationException($"Unexpected worker response '{response.Type}'.\n{_output}")
                };
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_process.HasExited)
            {
                _process.Dispose();
                _commandLock.Dispose();
                return;
            }

            try
            {
                await _commandLock.WaitAsync();
                try
                {
                    await WriteCommandAsync(new WorkerCommand("shutdown", null, null));
                    await ReadControlResponseAsync();
                }
                finally
                {
                    _commandLock.Release();
                }

                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            finally
            {
                _process.Dispose();
                _commandLock.Dispose();
            }
        }

        private async Task WriteCommandAsync(WorkerCommand command)
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command, JsonOptions));
            await _process.StandardInput.FlushAsync();
        }

        private async Task<WorkerResponse> ReadControlResponseAsync()
        {
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (line is null)
                {
                    var stderr = await _process.StandardError.ReadToEndAsync();
                    throw new InvalidOperationException($"Worker exited unexpectedly.\nSTDOUT:\n{_output}\nSTDERR:\n{stderr}");
                }

                _output.AppendLine(line);
                if (!line.StartsWith(ControlPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var response = JsonSerializer.Deserialize<WorkerResponse>(
                    line[ControlPrefix.Length..],
                    JsonOptions);
                return response ?? throw new InvalidOperationException($"Invalid worker control line '{line}'.");
            }
        }
    }

    private sealed record WorkerCommand(string Type, string? Key, string? Text);

    private sealed record WorkerResponse(string Type, string? Result, string? Error);

    private sealed record WorkerMembershipRecord(string NodeName, string HealthStatus);
}
