namespace OrleansReplicaKernel.Tests.TestSupport;

internal static class AsyncTestSync
{
    public static async Task YieldUntilDispatchAsync(int iterations = 8, bool includeDispatchBackoff = true)
    {
        if (iterations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), "Dispatch yield iterations must be non-negative.");
        }

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            await Task.Yield();
        }

        if (includeDispatchBackoff)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1));
        }
    }
}
