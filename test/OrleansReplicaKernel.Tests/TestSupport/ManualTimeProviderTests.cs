namespace OrleansReplicaKernel.Tests.TestSupport;

public sealed class ManualTimeProviderTests
{
    [Fact]
    public void TimestampAndElapsedTime_FollowManualAdvances()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 17, 0, 0, 0, TimeSpan.Zero));

        var startedAt = timeProvider.GetTimestamp();

        timeProvider.Advance(TimeSpan.FromMilliseconds(25));
        var checkpoint = timeProvider.GetTimestamp();

        timeProvider.Advance(TimeSpan.FromMilliseconds(15));
        var finishedAt = timeProvider.GetTimestamp();

        Assert.Equal(TimeSpan.TicksPerSecond, timeProvider.TimestampFrequency);
        Assert.Equal(TimeSpan.FromMilliseconds(25), timeProvider.GetElapsedTime(startedAt, checkpoint));
        Assert.Equal(TimeSpan.FromMilliseconds(40), timeProvider.GetElapsedTime(startedAt, finishedAt));
    }
}
