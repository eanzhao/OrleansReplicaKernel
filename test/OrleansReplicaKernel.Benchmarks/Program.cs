using System.Diagnostics;
using OrleansReplicaKernel.Demo;
using OrleansReplicaKernel.Identity;
using OrleansReplicaKernel.Messaging;
using OrleansReplicaKernel.Serialization;

var serializer = new BinarySerializerBuilder()
    .AddCodecsFromAssembly(typeof(EchoGrain).Assembly)
    .Build();
var messageSerializer = new BinaryMessageSerializer(serializer);
var message = new InvocationMessage(
    Guid.NewGuid(),
    Guid.NewGuid(),
    Guid.NewGuid(),
    AttemptSequence: 1,
    CreatedUtc: DateTimeOffset.UtcNow,
    SourceNodeName: "bench-source",
    Target: new GrainAddress("bench-target", new GrainId("echo", "bench"), OwnerVersion: 11),
    Invokable: new EchoPingSlowInvokable("benchmark-serialization-runtime", 25));

const int warmupIterations = 20_000;
const int measuredIterations = 200_000;

RunRoundTrips(messageSerializer, message, warmupIterations);
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

var stopwatch = Stopwatch.StartNew();
RunRoundTrips(messageSerializer, message, measuredIterations);
stopwatch.Stop();

var nanosecondsPerRoundTrip = stopwatch.Elapsed.TotalMilliseconds * 1_000_000d / measuredIterations;
var throughput = measuredIterations / stopwatch.Elapsed.TotalSeconds;

Console.WriteLine("InvocationMessage binary round-trip benchmark");
Console.WriteLine($"Iterations: {measuredIterations:N0}");
Console.WriteLine($"Elapsed: {stopwatch.Elapsed.TotalMilliseconds:N2} ms");
Console.WriteLine($"Mean: {nanosecondsPerRoundTrip:N0} ns/op");
Console.WriteLine($"Throughput: {throughput:N0} round-trips/sec");

static void RunRoundTrips(BinaryMessageSerializer messageSerializer, InvocationMessage message, int iterations)
{
    for (var i = 0; i < iterations; i++)
    {
        var bytes = messageSerializer.SerializeInvocationMessage(message);
        _ = messageSerializer.DeserializeInvocationMessage(bytes);
    }
}
