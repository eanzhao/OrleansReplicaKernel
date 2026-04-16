using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

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

        public static async Task<WorkerProcess> StartAsync(
            string nodeName,
            IReadOnlyDictionary<string, int> endpoints,
            params string[] seededOwners)
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
}
