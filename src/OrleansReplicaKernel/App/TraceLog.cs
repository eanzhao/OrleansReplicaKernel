namespace OrleansReplicaKernel.App;

internal static class TraceLog
{
    private static readonly Lock Gate = new();

    public static void Write(string area, string message)
    {
        lock (Gate)
        {
            Console.WriteLine($"[{area}] {message}");
        }
    }
}
