using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Cosmoteer.Game.Multiplayer;
using HarmonyLib;
using Xunit;

namespace UnofficialPatch.Tests;

public sealed class SpCatchUpTests
{
    private const double T = 1.0 / 30; // FpsTarget30 and 30 physics updates per second

    /// <summary>NetManager.cs:372-375 for FpsTarget30.</summary>
    private static double VanillaDt(double dt) => Math.Min(dt > T ? T * T / dt : dt, T);

    [Fact]
    public void OptInIsOnlyOne()
    {
        Assert.False(SpCatchUp.IsRequested("0"));
        Assert.False(SpCatchUp.IsRequested(null));
        Assert.False(SpCatchUp.IsRequested("true"));
        Assert.True(SpCatchUp.IsRequested("1"));
    }

    [Fact]
    public void DebtResetsOnNewManagerSpeedChangePauseAndDebugPacing()
    {
        Assert.False(SpCatchUp.MustReset(true, 8f, 8f, false));
        Assert.True(SpCatchUp.MustReset(false, 8f, 8f, false)); // new SPManager (or collected owner)
        Assert.True(SpCatchUp.MustReset(true, 8f, 1f, false));  // x1 -> x8: real-time debt would become 8x sim time
        Assert.True(SpCatchUp.MustReset(true, 1f, 8f, false));  // x8 -> x1
        Assert.True(SpCatchUp.MustReset(true, 0f, 0f, false));  // paused
        Assert.True(SpCatchUp.MustReset(true, float.NaN, float.NaN, false));
        Assert.True(SpCatchUp.MustReset(true, 8f, 8f, true));   // tick step / full throttle
    }

    [Fact]
    public void TranspilerReplacesOnlyTheDeltaTimeCall()
    {
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(SpCatchUp.Target);
        List<CodeInstruction> code = SpCatchUp.Transpiler(PatchProcessor.GetOriginalInstructions(SpCatchUp.Target)).ToList();
        Assert.Equal(original.Count, code.Count);
        Assert.Equal(1, original.Count(ci => ci.Calls(SpCatchUp.Vanilla)));
        Assert.DoesNotContain(code, ci => ci.Calls(SpCatchUp.Vanilla));
        int i = original.FindIndex(ci => ci.Calls(SpCatchUp.Vanilla));
        Assert.True(code[i].Calls(AccessTools.Method(typeof(SpCatchUp), nameof(SpCatchUp.DeltaTime))));
        Assert.Equal(OpCodes.Call, code[i].opcode);
        for (int k = 0; k < code.Count; k++)
            if (k != i) Assert.Equal(original[k].ToString(), code[k].ToString());
    }

    [Fact]
    public void TranspilerRejectsChangedIl()
    {
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(SpCatchUp.Target);
        original.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => SpCatchUp.Transpiler(original).ToList());
    }

    [Fact]
    public void PatchesOnlySinglePlayerManager()
    {
        Assert.Equal(typeof(SPManager), SpCatchUp.Target.DeclaringType);
        Assert.True(typeof(SPManager).IsSealed);
        Assert.False(typeof(RecordableSPManager).IsSubclassOf(typeof(SPManager)));
    }

    [Fact]
    public void WithoutDebtStepIsVanilla()
    {
        double debt = 0;
        foreach (double dt in new[] { 0.004, 0.016, 0.021, T, 0.0 })
        {
            Assert.Equal(VanillaDt(dt), SpCatchUp.Step(ref debt, dt, VanillaDt(dt), T));
            Assert.Equal(0, debt);
        }
    }

    [Fact]
    public void SlowFrameIsRepaidLaterAndNoFrameExceedsCap()
    {
        double debt = 0, given = 0, real = 0;
        double[] frames = [0.021, 0.088, 0.021, 0.021, 0.021, 0.021, 0.045, 0.021, 0.021, 0.021, 0.021, 0.021];
        foreach (double dt in frames)
        {
            double r = SpCatchUp.Step(ref debt, dt, VanillaDt(dt), T);
            Assert.True(r <= T + 1e-12);
            Assert.True(r >= VanillaDt(dt));
            given += r; real += dt;
        }
        Assert.Equal(0, debt, 12);
        Assert.Equal(real, given, 12); // no game time lost
    }

    [Fact]
    public void DebtIsBoundedUnderSustainedOverload()
    {
        double debt = 0;
        for (int i = 0; i < 1000; i++)
        {
            double r = SpCatchUp.Step(ref debt, 0.1, VanillaDt(0.1), T);
            Assert.Equal(T, r, 12);
            Assert.True(debt <= SpCatchUp.MaxDebt);
        }
        Assert.Equal(SpCatchUp.MaxDebt - (T - VanillaDt(0.1)), debt, 12); // saturated; the rest is dropped as in vanilla
    }

    [Fact]
    public void LowerTargetCapIsRespected()
    {
        double cap = 1.0 / 60, debt = 0.1;
        double v = Math.Min(0.010, cap);
        Assert.Equal(cap, SpCatchUp.Step(ref debt, 0.010, v, cap), 12);
        Assert.Equal(0.1 - (cap - 0.010), debt, 12);
    }
}
