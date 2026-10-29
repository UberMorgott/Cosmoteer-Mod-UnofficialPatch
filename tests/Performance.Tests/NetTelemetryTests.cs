using Xunit;

namespace Performance.Tests;

public class NetTelemetryTests
{
    [Fact]
    public void TargetsResolve()
    {
        Assert.NotNull(InputArrivalTelemetry.Target);
        Assert.NotNull(StallTelemetry.Target);
    }

    [Fact]
    public void OneLongStallSpanningSummariesCountsOnceWithItsFullLength()
    {
        var c = new NetTelemetry.StallClock();
        c.Check(true, 0);
        c.Check(false, 5); // stall starts
        c.Check(false, 8); // still waiting: start kept
        c.Account(10);
        Assert.Equal((1, 5L, 5L), (c.Count, c.Total, c.Max));
        c.ResetInterval();

        c.Account(20);
        Assert.Equal((0, 10L, 15L), (c.Count, c.Total, c.Max)); // no new stall; max from the real start
        c.ResetInterval();

        c.Check(true, 30); // ends: 25 long
        c.Check(false, 40);
        c.Check(true, 42);
        Assert.Equal((1, 12L, 25L), (c.Count, c.Total, c.Max));
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(5, 4)]
    [InlineData(6, 5)]
    public void SlackBuckets(int slack, int bucket) => Assert.Equal(bucket, NetTelemetry.Bucket(slack));
}
