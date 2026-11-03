using Xunit;

namespace UnofficialPatch.Tests;

public sealed class TickBudgetTests
{
    private const double I = 1.0 / 30;            // tick interval
    private const double B = TickBudget.Share / 60; // budget at 60 fps target

    [Fact]
    public void OnUnlessKilled()
    {
        Assert.True(TickBudget.IsRequested(null));
        Assert.True(TickBudget.IsRequested("1"));
        Assert.False(TickBudget.IsRequested("0"));
    }

    [Fact]
    public void X1SlowMotionPauseAndUnknownCostAreUntouched()
    {
        foreach (double scale in new[] { 1.0, 0.5, 0.0, double.NaN })
            Assert.Equal(0.0167, TickBudget.Cap(0.0167, 0.0167, scale, 0.035, B, I));
        Assert.Equal(0.0167, TickBudget.Cap(0.0167, 0.0167, 8, 0, B, I));
    }

    [Fact]
    public void CheapTicksAtX8AreUntouched()
    {
        // 1 ms ticks: budget fits ~10.8 ticks, x8 at 60 fps wants ~4.
        Assert.Equal(1.0 / 60, TickBudget.Cap(1.0 / 60, 1.0 / 60, 8, 0.001, B, I), 12);
    }

    [Fact]
    public void ExpensiveTicksAtX8GetNoMoreGameTimeThanX1()
    {
        double vanilla = 1.0 / 60, real = TickBudget.Cap(vanilla, vanilla, 8, 0.035, B, I);
        Assert.Equal(vanilla, real * 8, 12); // x1's game time: same ticks per frame as x1
    }

    [Fact]
    public void MediumTicksFillTheBudget()
    {
        double cost = 0.002, real = TickBudget.Cap(1.0 / 30, 1.0 / 60, 8, cost, B, I);
        Assert.Equal(B, real * 8 / I * cost, 12); // ticks this frame x cost = budget
    }

    [Fact]
    public void CatchUpNeverExceedsTheCap()
    {
        double capped = TickBudget.Cap(1.0 / 60, 1.0 / 60, 8, 0.005, B, I);
        Assert.Equal(capped, TickBudget.Cap(1.0 / 30, 1.0 / 60, 8, 0.005, B, I), 12);
    }

    [Fact]
    public void X8ThatFitsTheFrameIsUntouched()
    {
        // Codex counterexample: 60 fps, 3 ms ticks, 2 ms other work: 4 ticks (12 ms) fit 16.7 ms.
        double frame = 1.0 / 60, budget = TickBudget.Budget(frame, 0.002);
        Assert.Equal(frame, TickBudget.Cap(frame, frame, 8, 0.003, budget, I), 12);
    }

    [Fact]
    public void BudgetIsTheFrameRestButAtLeastTheShare()
    {
        Assert.Equal(1.0 / 60 - 0.002, TickBudget.Budget(1.0 / 60, 0.002), 12);
        Assert.Equal(TickBudget.Share / 60, TickBudget.Budget(1.0 / 60, 0.015), 12);
        Assert.Equal(1.0 / 60, TickBudget.Budget(1.0 / 60, 0), 12);
    }

    [Fact]
    public void AverageStartsAtFirstSampleThenSmooths()
    {
        Assert.Equal(0.02, TickBudget.Average(0, 0.02));
        Assert.Equal(0.02 + TickBudget.Alpha * 0.01, TickBudget.Average(0.02, 0.03), 12);
    }
}
