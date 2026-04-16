using BenchmarkDotNet.Attributes;
using OrleansReplicaKernel.Demo;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Runtime;
using OrleansReplicaKernel.Serialization;

namespace OrleansReplicaKernel.Benchmarks;

[MemoryDiagnoser]
public class SerializationBenchmarks
{
    private BinarySerializer _serializer = null!;
    private BinaryMessageSerializer _messageSerializer = null!;
    private InvocationMessage _invocationMessage = null!;
    private InvocationResponseMessage _invocationResponse = null!;
    private GrainId _grainId;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _serializer = new BinarySerializerBuilder()
            .AddCodecsFromAssembly(typeof(EchoGrain).Assembly)
            .Build();
        _messageSerializer = new BinaryMessageSerializer(_serializer);
        _grainId = new GrainId("echo", "benchmark-serialization");
        _invocationMessage = new InvocationMessage(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            AttemptSequence: 1,
            CreatedUtc: new DateTimeOffset(2026, 04, 16, 0, 0, 0, TimeSpan.Zero),
            SourceNodeName: "bench-source",
            Target: new GrainAddress("bench-target", _grainId, OwnerVersion: 7),
            Invokable: new EchoPingInvokable("benchmark-serialization"));
        _invocationResponse = new InvocationResponseMessage(
            RequestId: Guid.Parse("44444444-4444-4444-4444-444444444444"),
            AttemptId: Guid.Parse("55555555-5555-5555-5555-555555555555"),
            AttemptSequence: 1,
            ResponderNodeName: "bench-target",
            Result: "benchmark-response",
            Error: null);
    }

    [Benchmark]
    public InvocationMessage InvocationMessageRoundTrip()
    {
        var payload = _messageSerializer.SerializeInvocationMessage(_invocationMessage);
        return _messageSerializer.DeserializeInvocationMessage(payload);
    }

    [Benchmark]
    public InvocationResponseMessage InvocationResponseRoundTrip()
    {
        var payload = _messageSerializer.SerializeInvocationResponse(_invocationResponse);
        return _messageSerializer.DeserializeInvocationResponse(payload);
    }

    [Benchmark]
    public GrainId GrainIdRoundTrip()
    {
        var payload = _serializer.Serialize(_grainId);
        return _serializer.Deserialize<GrainId>(payload);
    }
}
