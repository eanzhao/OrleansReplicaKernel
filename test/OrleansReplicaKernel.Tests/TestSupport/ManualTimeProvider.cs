namespace OrleansReplicaKernel.Tests.TestSupport;

internal sealed class ManualTimeProvider : TimeProvider
{
    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTimeOffset UtcNow { get; private set; }

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan delta)
    {
        UtcNow = UtcNow.Add(delta);
    }
}
