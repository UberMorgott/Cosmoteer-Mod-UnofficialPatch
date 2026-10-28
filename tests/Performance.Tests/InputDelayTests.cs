using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

public class InputDelayTests
{
    private static Func<System.Collections.Generic.IEnumerable<CodeInstruction>, System.Collections.Generic.IEnumerable<CodeInstruction>> Pin(Type patch) =>
        patch == typeof(InputDelay) ? InputDelay.Transpiler : InputDelaySamples.Transpiler;

    private static MethodBase Target(Type patch) => patch == typeof(InputDelay) ? InputDelay.Target : InputDelaySamples.Target;

    [Theory]
    [InlineData(typeof(InputDelay))]
    [InlineData(typeof(InputDelaySamples))]
    public void TranspilerMatchesRealGameIlUnchanged(Type patch)
    {
        var original = PatchProcessor.GetOriginalInstructions(Target(patch));

        Assert.Equal(original.Select(i => i.ToString()), Pin(patch)(original).Select(i => i.ToString()));
    }

    [Theory]
    [InlineData(typeof(InputDelay))]
    [InlineData(typeof(InputDelaySamples))]
    public void TranspilerRejectsChangedIl(Type patch)
    {
        var original = PatchProcessor.GetOriginalInstructions(Target(patch));
        original.Insert(0, new CodeInstruction(OpCodes.Nop));

        Assert.Throws<InvalidOperationException>(() => Pin(patch)(original).ToList());
    }

    [Fact]
    public void TelemetryTargetsResolve()
    {
        Assert.NotNull(InputArrivalTelemetry.Target);
        Assert.NotNull(StallTelemetry.Target);
    }

    [Fact]
    public void KeepsTheTwoLargestOrSmallestAndSkipsMissingSamples()
    {
        double[] values = [0.05, double.NaN, 0.2, 0.01, 0.12];
        Span<double> worst = [double.NaN, double.NaN], best = [double.NaN, double.NaN];
        foreach (double v in values)
        {
            InputDelay.Insert(worst, v, largest: true);
            InputDelay.Insert(best, v, largest: false);
        }

        Assert.Equal([0.2, 0.12], worst.ToArray());
        Assert.Equal([0.01, 0.05], best.ToArray());
        Assert.Equal(0.32, InputDelay.Sum(worst), 9);
    }

    [Fact]
    public void SingleConnectionSumsJustThatOne()
    {
        Span<double> worst = [double.NaN, double.NaN];
        InputDelay.Insert(worst, 0.1, largest: true);

        Assert.Equal(0.1, InputDelay.Sum(worst), 9);
    }

    [Theory]
    [InlineData(0.0, 2)] // floor at Min
    [InlineData(0.1, 3)] // float math like vanilla: in double 0.1 x 30 = 3.0000000000000004 would ceil to 4
    [InlineData(0.101, 4)]
    [InlineData(5.0, 60)] // clamp at Max
    [InlineData(double.PositiveInfinity, 60)] // total loss saturates to Max, not to an invalid int
    [InlineData(double.NaN, 2)]
    public void TicksCeilsClampsAndSaturates(double seconds, int expected) =>
        Assert.Equal(expected, InputDelay.Ticks(seconds, 30f, 2, 60));

    [Fact]
    public void TotalLossIsInfiniteNotNegative()
    {
        Assert.Equal(double.PositiveInfinity, InputDelay.Estimate(0.05, 1f));
        Assert.Equal(0.1, InputDelay.Estimate(0.05, 0.5f), 9);
    }

    [Fact]
    public void HysteresisRaisesAtOnceAndLowersOneTickPerCalmHold()
    {
        var h = new InputDelay.Hysteresis();
        const long hold = 100;

        Assert.Equal(4, h.Next(4, true, 1, 0, hold));
        Assert.Equal(9, h.Next(9, true, 1, 1, hold)); // spike: up immediately
        Assert.Equal(9, h.Next(3, true, 1, 2, hold)); // hold starts
        Assert.Equal(9, h.Next(3, true, 1, 101, hold));
        Assert.Equal(8, h.Next(3, true, 1, 102, hold)); // one step per hold
        Assert.Equal(8, h.Next(3, true, 1, 201, hold));
        Assert.Equal(7, h.Next(3, true, 1, 202, hold));
    }

    [Fact]
    public void HysteresisHoldResetsOnHigherTargetStaleSamplesOrConnectionChange()
    {
        var h = new InputDelay.Hysteresis();
        const long hold = 100;
        h.Next(9, true, 1, 0, hold);

        h.Next(3, true, 1, 10, hold);
        h.Next(9, true, 1, 50, hold); // target back at current: hold restarts
        h.Next(3, true, 1, 60, hold);
        Assert.Equal(9, h.Next(3, true, 1, 150, hold));
        Assert.Equal(9, h.Next(3, false, 1, 161, hold)); // stale/insufficient samples: never lower, hold restarts
        h.Next(3, true, 1, 170, hold);
        Assert.Equal(9, h.Next(3, true, 2, 300, hold)); // connection set changed: hold restarts
        h.Next(3, true, 2, 310, hold);
        Assert.Equal(8, h.Next(3, true, 2, 410, hold));
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
