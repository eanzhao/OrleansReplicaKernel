using OrleansReplicaKernel.Demo;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Invocation;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Serialization;

namespace OrleansReplicaKernel.Tests.Serialization;

public sealed class BinarySerializationTests
{
    [Fact]
    public void InvocationMessage_CanRoundTrip_ThroughBinaryWireFormat()
    {
        var serializer = CreateSerializer();
        var requestId = Guid.NewGuid();
        var requestChainId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var message = new InvocationMessage(
            requestId,
            requestChainId,
            attemptId,
            AttemptSequence: 2,
            CreatedUtc: new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero),
            SourceNodeName: "dev-node-1",
            Target: new GrainAddress("dev-node-2", new GrainId("echo", "binary-roundtrip"), OwnerVersion: 7),
            Invokable: new EchoPingSlowInvokable("hello-binary", 42),
            SourceKind: InvocationSourceKind.Client);

        var roundTripped = serializer.Deserialize<InvocationMessage>(serializer.Serialize(message));

        Assert.Equal(requestId, roundTripped.RequestId);
        Assert.Equal(requestChainId, roundTripped.RequestChainId);
        Assert.Equal(attemptId, roundTripped.AttemptId);
        Assert.Equal(2, roundTripped.AttemptSequence);
        Assert.Equal("dev-node-1", roundTripped.SourceNodeName);
        Assert.Equal(InvocationSourceKind.Client, roundTripped.SourceKind);
        Assert.Equal(new GrainAddress("dev-node-2", new GrainId("echo", "binary-roundtrip"), 7), roundTripped.Target);

        var invokable = Assert.IsType<EchoPingSlowInvokable>(roundTripped.Invokable);
        Assert.Equal("hello-binary", invokable.Text);
        Assert.Equal(42, invokable.DelayMs);
    }

    [Fact]
    public void InvocationResponse_CanCarry_CustomPayload_WithRegisteredCodec()
    {
        var serializer = CreateSerializer(builder => builder.AddCodec(new CustomPayloadCodec()));
        var payload = new CustomPayload(
            CorrelationId: Guid.NewGuid(),
            Message: "custom-payload",
            Timestamp: new DateTimeOffset(2026, 4, 16, 12, 34, 56, TimeSpan.FromHours(8)),
            AttemptHistory: [1, 2, 3, 5],
            Metadata: new Dictionary<string, int>
            {
                ["attempts"] = 4,
                ["fanout"] = 2
            },
            Tags: ["serialization", "runtime"],
            Description: null,
            RetryBudget: 9);
        var response = new InvocationResponseMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            AttemptSequence: 3,
            ResponderNodeName: "dev-node-2",
            Result: payload,
            Error: null);

        var roundTripped = serializer.Deserialize<InvocationResponseMessage>(serializer.Serialize(response));

        var restoredPayload = Assert.IsType<CustomPayload>(roundTripped.Result);
        Assert.Equal(payload.CorrelationId, restoredPayload.CorrelationId);
        Assert.Equal(payload.Message, restoredPayload.Message);
        Assert.Equal(payload.Timestamp, restoredPayload.Timestamp);
        Assert.Equal(payload.AttemptHistory, restoredPayload.AttemptHistory);
        Assert.Equal(payload.Metadata, restoredPayload.Metadata);
        Assert.Equal(payload.Tags, restoredPayload.Tags);
        Assert.Null(restoredPayload.Description);
        Assert.Equal(payload.RetryBudget, restoredPayload.RetryBudget);
        Assert.Null(roundTripped.Error);
    }

    [Fact]
    public void VersionedCodec_CanSkip_UnknownFields()
    {
        var writerSerializer = new BinarySerializerBuilder()
            .AddCodec(new VersionedGreetingV2Codec())
            .Build();
        var readerSerializer = new BinarySerializerBuilder()
            .AddCodec(new VersionedGreetingV1Codec())
            .Build();

        var bytes = writerSerializer.Serialize(new VersionedGreetingV2("compatible", Revision: 4, Tags: ["warm", "handoff"]));
        var restored = readerSerializer.Deserialize<VersionedGreetingV1>(bytes);

        Assert.Equal("compatible", restored.Message);
    }

    [Fact]
    public void InvocationResponse_Retains_RemoteExceptionSemantics_NeededByRuntime()
    {
        var serializer = CreateSerializer();
        var staleResponse = new InvocationResponseMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            AttemptSequence: 2,
            ResponderNodeName: "dev-node-2",
            Result: null,
            Error: new StaleGrainAddressException(
                new GrainId("echo", "stale-address"),
                "dev-node-2",
                messageOwnerVersion: 3,
                fencedOwnerVersion: 5));

        var staleRoundTripped = serializer.Deserialize<InvocationResponseMessage>(serializer.Serialize(staleResponse));
        var stale = Assert.IsType<StaleGrainAddressException>(staleRoundTripped.Error);
        Assert.Equal(new GrainId("echo", "stale-address"), stale.GrainId);
        Assert.Equal("dev-node-2", stale.NodeName);
        Assert.Equal(3, stale.MessageOwnerVersion);
        Assert.Equal(5, stale.FencedOwnerVersion);

        var invalidOperationResponse = new InvocationResponseMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            AttemptSequence: 1,
            ResponderNodeName: "dev-node-3",
            Result: null,
            Error: new InvalidOperationException("No callback target registered for 'echo-observer/callback'."));

        var invalidOperationRoundTripped =
            serializer.Deserialize<InvocationResponseMessage>(serializer.Serialize(invalidOperationResponse));
        var invalidOperation = Assert.IsType<InvalidOperationException>(invalidOperationRoundTripped.Error);
        Assert.Contains("No callback target registered", invalidOperation.Message);
    }

    private static BinarySerializer CreateSerializer(Action<BinarySerializerBuilder>? configure = null)
    {
        var builder = new BinarySerializerBuilder()
            .AddCodecsFromAssembly(typeof(EchoGrain).Assembly);
        configure?.Invoke(builder);
        return builder.Build();
    }

    private sealed record CustomPayload(
        Guid CorrelationId,
        string Message,
        DateTimeOffset Timestamp,
        List<int> AttemptHistory,
        Dictionary<string, int> Metadata,
        HashSet<string> Tags,
        string? Description,
        int? RetryBudget);

    private sealed class CustomPayloadCodec : BinaryObjectCodec<CustomPayload>
    {
        public override string Alias => "test.custom-payload";

        protected override CustomPayload ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
        {
            Guid correlationId = Guid.Empty;
            string message = string.Empty;
            DateTimeOffset timestamp = default;
            List<int> attemptHistory = [];
            Dictionary<string, int> metadata = [];
            HashSet<string> tags = [];
            string? description = null;
            int? retryBudget = null;

            while (reader.TryReadField(out var fieldId, out var payload))
            {
                switch (fieldId)
                {
                    case 1:
                        correlationId = serializer.Read<Guid>(payload);
                        break;
                    case 2:
                        message = serializer.Read<string>(payload);
                        break;
                    case 3:
                        timestamp = serializer.Read<DateTimeOffset>(payload);
                        break;
                    case 4:
                        attemptHistory = serializer.Read<List<int>>(payload);
                        break;
                    case 5:
                        metadata = serializer.Read<Dictionary<string, int>>(payload);
                        break;
                    case 6:
                        tags = serializer.Read<HashSet<string>>(payload);
                        break;
                    case 7:
                        description = serializer.ReadOptional<string>(payload);
                        break;
                    case 8:
                        retryBudget = serializer.ReadNullable<int>(payload);
                        break;
                }
            }

            return new CustomPayload(correlationId, message, timestamp, attemptHistory, metadata, tags, description, retryBudget);
        }

        protected override void WriteFields(BinaryObjectWriter writer, CustomPayload value, BinarySerializer serializer)
        {
            writer.WriteField(1, value.CorrelationId, serializer);
            writer.WriteField(2, value.Message, serializer);
            writer.WriteField(3, value.Timestamp, serializer);
            writer.WriteField(4, value.AttemptHistory, serializer);
            writer.WriteField(5, value.Metadata, serializer);
            writer.WriteField(6, value.Tags, serializer);
            writer.WriteOptionalField(7, value.Description, serializer);
            writer.WriteNullableField(8, value.RetryBudget, serializer);
        }
    }

    private sealed record VersionedGreetingV1(string Message);

    private sealed record VersionedGreetingV2(string Message, int Revision, List<string> Tags);

    private sealed class VersionedGreetingV1Codec : BinaryObjectCodec<VersionedGreetingV1>
    {
        public override string Alias => "test.versioned-greeting";

        protected override VersionedGreetingV1 ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
        {
            string message = string.Empty;

            while (reader.TryReadField(out var fieldId, out var payload))
            {
                if (fieldId == 1)
                {
                    message = serializer.Read<string>(payload);
                }
            }

            return new VersionedGreetingV1(message);
        }

        protected override void WriteFields(BinaryObjectWriter writer, VersionedGreetingV1 value, BinarySerializer serializer)
        {
            writer.WriteField(1, value.Message, serializer);
        }
    }

    private sealed class VersionedGreetingV2Codec : BinaryObjectCodec<VersionedGreetingV2>
    {
        public override string Alias => "test.versioned-greeting";

        protected override VersionedGreetingV2 ReadFields(ref BinaryObjectReader reader, BinarySerializer serializer)
        {
            string message = string.Empty;
            int revision = 0;
            List<string> tags = [];

            while (reader.TryReadField(out var fieldId, out var payload))
            {
                switch (fieldId)
                {
                    case 1:
                        message = serializer.Read<string>(payload);
                        break;
                    case 2:
                        revision = serializer.Read<int>(payload);
                        break;
                    case 3:
                        tags = serializer.Read<List<string>>(payload);
                        break;
                }
            }

            return new VersionedGreetingV2(message, revision, tags);
        }

        protected override void WriteFields(BinaryObjectWriter writer, VersionedGreetingV2 value, BinarySerializer serializer)
        {
            writer.WriteField(1, value.Message, serializer);
            writer.WriteField(2, value.Revision, serializer);
            writer.WriteField(3, value.Tags, serializer);
        }
    }
}
