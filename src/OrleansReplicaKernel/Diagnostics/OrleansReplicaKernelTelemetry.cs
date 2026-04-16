using System.Diagnostics;
using System.Diagnostics.Metrics;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Runtime;

namespace OrleansReplicaKernel.Diagnostics;

public static class OrleansReplicaKernelTelemetry
{
    public const string MeterName = "OrleansReplicaKernel";
    public const string ActivitySourceName = "OrleansReplicaKernel.Runtime";

    public static Meter Meter { get; } = new(MeterName);

    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    private static readonly UpDownCounter<long> ActivationCountInstrument =
        Meter.CreateUpDownCounter<long>("orleans-replica-kernel.activation.count");

    private static readonly Histogram<double> TurnDurationInstrument =
        Meter.CreateHistogram<double>("orleans-replica-kernel.turn.duration", unit: "ms");

    private static readonly Histogram<double> MessageLatencyInstrument =
        Meter.CreateHistogram<double>("orleans-replica-kernel.message.latency", unit: "ms");

    private static readonly Counter<long> MembershipChangeInstrument =
        Meter.CreateCounter<long>("orleans-replica-kernel.membership.changes");

    public static Activity? StartInvokeActivity(
        string localNodeName,
        GrainId grainId,
        IInvokable invokable,
        InvocationSourceKind sourceKind,
        Guid requestId)
    {
        var tags = BuildCommonTags(localNodeName, grainId, invokable, sourceKind, requestId);
        tags.Add("messaging.operation", "send");
        tags.Add("rpc.system", "orleans");

        return ActivitySource.StartActivity(
            "orleans.invoke",
            ActivityKind.Client,
            default(ActivityContext),
            tags);
    }

    public static Activity? StartReceiveActivity(InvocationMessage message, string localNodeName)
    {
        var tags = BuildCommonTags(
            localNodeName,
            message.Target.GrainId,
            message.Invokable,
            message.SourceKind,
            message.RequestId);
        tags.Add("messaging.operation", "receive");
        tags.Add("messaging.source.name", message.SourceNodeName);
        tags.Add("messaging.destination.name", message.Target.NodeName);
        tags.Add("orleans.attempt.sequence", message.AttemptSequence);
        tags.Add("rpc.system", "orleans");

        if (TryParseContext(message, out var parentContext))
        {
            return ActivitySource.StartActivity(
                "orleans.receive",
                ActivityKind.Server,
                parentContext,
                tags);
        }

        return ActivitySource.StartActivity(
            "orleans.receive",
            ActivityKind.Server,
            default(ActivityContext),
            tags);
    }

    public static InvocationMessage StampCurrentTraceContext(InvocationMessage message)
        => message with
        {
            TraceParent = FormatTraceParent(Activity.Current),
            TraceState = Activity.Current?.TraceStateString
        };

    public static void RecordActivationDelta(long delta, GrainId grainId, string nodeName)
    {
        ActivationCountInstrument.Add(
            delta,
            new KeyValuePair<string, object?>("orleans.node.name", nodeName),
            new KeyValuePair<string, object?>("orleans.grain.type", grainId.GrainType));
    }

    public static void RecordTurnDuration(TimeSpan duration, GrainId grainId, string methodName, string nodeName)
    {
        TurnDurationInstrument.Record(
            duration.TotalMilliseconds,
            new KeyValuePair<string, object?>("orleans.node.name", nodeName),
            new KeyValuePair<string, object?>("orleans.grain.type", grainId.GrainType),
            new KeyValuePair<string, object?>("rpc.method", methodName));
    }

    public static void RecordMessageLatency(
        InvocationMessage message,
        string localNodeName,
        DateTimeOffset utcNow)
    {
        if (message.CreatedUtc == default || utcNow < message.CreatedUtc)
        {
            return;
        }

        MessageLatencyInstrument.Record(
            (utcNow - message.CreatedUtc).TotalMilliseconds,
            new KeyValuePair<string, object?>("orleans.node.name", localNodeName),
            new KeyValuePair<string, object?>("orleans.grain.type", message.Target.GrainId.GrainType),
            new KeyValuePair<string, object?>("orleans.source.kind", message.SourceKind.ToString()));
    }

    public static void RecordMembershipChange(
        string localNodeName,
        string nodeName,
        NodeHealthStatus? previousStatus,
        NodeHealthStatus currentStatus)
    {
        MembershipChangeInstrument.Add(
            1,
            new KeyValuePair<string, object?>("orleans.node.name", localNodeName),
            new KeyValuePair<string, object?>("orleans.member.name", nodeName),
            new KeyValuePair<string, object?>("orleans.membership.previous", previousStatus?.ToString() ?? "<none>"),
            new KeyValuePair<string, object?>("orleans.membership.current", currentStatus.ToString()));
    }

    public static string? FormatTraceParent(Activity? activity)
    {
        if (activity is null)
        {
            return null;
        }

        var context = activity.Context;
        return $"00-{context.TraceId}-{context.SpanId}-{((byte)context.TraceFlags):x2}";
    }

    private static bool TryParseContext(InvocationMessage message, out ActivityContext parentContext)
    {
        if (!string.IsNullOrWhiteSpace(message.TraceParent)
            && ActivityContext.TryParse(message.TraceParent, message.TraceState, out parentContext))
        {
            return true;
        }

        parentContext = default;
        return false;
    }

    private static ActivityTagsCollection BuildCommonTags(
        string localNodeName,
        GrainId grainId,
        IInvokable invokable,
        InvocationSourceKind sourceKind,
        Guid requestId)
        => new()
        {
            { "orleans.node.name", localNodeName },
            { "orleans.grain.id", grainId.ToString() },
            { "orleans.grain.type", grainId.GrainType },
            { "rpc.service", invokable.InterfaceName },
            { "rpc.method", invokable.MethodName },
            { "orleans.request.id", requestId.ToString("N") },
            { "orleans.source.kind", sourceKind.ToString() }
        };
}
