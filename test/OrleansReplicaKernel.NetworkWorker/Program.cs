using System.Net;
using System.Text.Json;
using OrleansReplicaKernel.App;
using OrleansReplicaKernel.Demo;

const string controlPrefix = "@@worker@@";
var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var options = WorkerOptions.Parse(args);

var builder = new OrleansReplicaKernelBuilder()
    .AddGeneratedGrainImplementationsFromAssembly(typeof(EchoGrain).Assembly)
    .AddGeneratedGrainReferencesFromAssembly(typeof(EchoGrainReference).Assembly)
    .AddGeneratedObjectReferencesFromAssembly(typeof(EchoObserverReference).Assembly)
    .UseTcpTransport()
    .WithTcpHeartbeatInterval(TimeSpan.FromMilliseconds(options.HeartbeatMilliseconds));

if (!string.IsNullOrWhiteSpace(options.MembershipFile))
{
    builder.UseFileMembershipTable(options.MembershipFile);
}

if (!string.IsNullOrWhiteSpace(options.DirectoryFile))
{
    builder.UseFileGrainDirectoryTable(options.DirectoryFile);
}

foreach (var endpoint in options.Endpoints)
{
    builder.WithTcpNodeEndpoint(endpoint.Key, new IPEndPoint(IPAddress.Loopback, endpoint.Value));
}

foreach (var seed in options.SeededOwners)
{
    builder.SeedGrainOwner(seed.GrainType, seed.Key, seed.OwnerNodeName, seed.Version);
}

await using var host = builder.Build(
    options.NodeName,
    options.Endpoints.Keys
        .Where(item => !string.Equals(item, options.NodeName, StringComparison.Ordinal))
        .ToArray());

WriteControl(new WorkerResponse("ready", null, null));

while (await Console.In.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    WorkerCommand command;
    try
    {
        command = JsonSerializer.Deserialize<WorkerCommand>(line, serializerOptions)
            ?? throw new InvalidOperationException("Worker command payload is empty.");
    }
    catch (Exception exception)
    {
        WriteControl(new WorkerResponse("error", null, $"invalid-command:{exception.Message}"));
        continue;
    }

    try
    {
        switch (command.Type)
        {
            case "ping":
                var grain = host.GetGrain<IEchoGrain>(command.Key ?? throw new InvalidOperationException("ping command requires key."));
                var result = await grain.PingAsync(command.Text ?? throw new InvalidOperationException("ping command requires text."));
                WriteControl(new WorkerResponse("result", result, null));
                break;
            case "membership":
                var membership = host.CaptureMembershipCheckpoint().ClusterMembership.Members
                    .Select(item => new WorkerMembershipRecord(item.NodeName, item.HealthStatus.ToString()))
                    .ToArray();
                WriteControl(new WorkerResponse("membership", JsonSerializer.Serialize(membership, serializerOptions), null));
                break;
            case "directory":
                WriteControl(new WorkerResponse("directory", host.DescribeGrainDirectory(), null));
                break;
            case "probe":
                var probeCount = await host.RunProbeTickAsync();
                WriteControl(new WorkerResponse("probe", probeCount.ToString(), null));
                break;
            case "shutdown":
                WriteControl(new WorkerResponse("shutdown", null, null));
                return;
            default:
                throw new InvalidOperationException($"unsupported-command:{command.Type}");
        }
    }
    catch (Exception exception)
    {
        WriteControl(new WorkerResponse("error", null, exception.GetType().Name + ":" + exception.Message));
    }
}

return;

void WriteControl(WorkerResponse response)
{
    Console.WriteLine(controlPrefix + JsonSerializer.Serialize(response, serializerOptions));
    Console.Out.Flush();
}

public sealed class NetworkWorkerAnchor;

internal sealed record WorkerCommand(string Type, string? Key, string? Text);

internal sealed record WorkerResponse(string Type, string? Result, string? Error);

internal sealed record WorkerMembershipRecord(string NodeName, string HealthStatus);

internal sealed record SeededOwner(string GrainType, string Key, string OwnerNodeName, long Version);

internal sealed class WorkerOptions
{
    public required string NodeName { get; init; }

    public required Dictionary<string, int> Endpoints { get; init; }

    public required List<SeededOwner> SeededOwners { get; init; }

    public required int HeartbeatMilliseconds { get; init; }

    public string? MembershipFile { get; init; }

    public string? DirectoryFile { get; init; }

    public static WorkerOptions Parse(string[] args)
    {
        var nodeName = string.Empty;
        var endpoints = new Dictionary<string, int>(StringComparer.Ordinal);
        var seededOwners = new List<SeededOwner>();
        var heartbeatMilliseconds = 50;
        string? membershipFile = null;
        string? directoryFile = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--node-name":
                    nodeName = args[++index];
                    break;
                case "--endpoint":
                    ParseEndpoint(args[++index], endpoints);
                    break;
                case "--seed-owner":
                    seededOwners.Add(ParseSeededOwner(args[++index]));
                    break;
                case "--heartbeat-ms":
                    heartbeatMilliseconds = int.Parse(args[++index]);
                    break;
                case "--membership-file":
                    membershipFile = args[++index];
                    break;
                case "--directory-file":
                    directoryFile = args[++index];
                    break;
                default:
                    throw new InvalidOperationException($"Unknown worker argument '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(nodeName))
        {
            throw new InvalidOperationException("Worker requires --node-name.");
        }

        if (!endpoints.ContainsKey(nodeName))
        {
            throw new InvalidOperationException($"Worker endpoint for '{nodeName}' is missing.");
        }

        return new WorkerOptions
        {
            NodeName = nodeName,
            Endpoints = endpoints,
            SeededOwners = seededOwners,
            HeartbeatMilliseconds = heartbeatMilliseconds,
            MembershipFile = membershipFile,
            DirectoryFile = directoryFile
        };
    }

    private static void ParseEndpoint(string argument, Dictionary<string, int> endpoints)
    {
        var separatorIndex = argument.IndexOf('=');
        if (separatorIndex <= 0 || separatorIndex == argument.Length - 1)
        {
            throw new InvalidOperationException($"Invalid --endpoint value '{argument}'.");
        }

        var nodeName = argument[..separatorIndex];
        var port = int.Parse(argument[(separatorIndex + 1)..]);
        endpoints[nodeName] = port;
    }

    private static SeededOwner ParseSeededOwner(string argument)
    {
        var assignmentIndex = argument.IndexOf('=');
        if (assignmentIndex <= 0 || assignmentIndex == argument.Length - 1)
        {
            throw new InvalidOperationException($"Invalid --seed-owner value '{argument}'.");
        }

        var left = argument[..assignmentIndex].Split(':', StringSplitOptions.TrimEntries);
        if (left.Length != 2)
        {
            throw new InvalidOperationException($"Invalid --seed-owner grain identity '{argument}'.");
        }

        return new SeededOwner(left[0], left[1], argument[(assignmentIndex + 1)..], Version: 1);
    }
}
