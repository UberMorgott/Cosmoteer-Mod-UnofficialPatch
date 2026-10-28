using System;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Cosmoteer.Game;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

public class PlayerMoneyTests
{
    private static PlayerState NewPlayer(int index)
    {
        var state = (PlayerState)RuntimeHelpers.GetUninitializedObject(typeof(PlayerState));
        typeof(PlayerState).GetProperty(nameof(PlayerState.Index))!.SetValue(state, index);
        return state;
    }

    [Theory]
    [InlineData(typeof(PlayerMoney))]
    [InlineData(typeof(PlayerMoneyFlush))]
    public void TranspilerMatchesRealGameIlUnchanged(Type patch)
    {
        var (target, transpile) = Pin(patch);
        var original = PatchProcessor.GetOriginalInstructions(target);

        Assert.Equal(original.Select(i => i.ToString()), transpile(original).Select(i => i.ToString()));
    }

    [Theory]
    [InlineData(typeof(PlayerMoney))]
    [InlineData(typeof(PlayerMoneyFlush))]
    public void TranspilerRejectsChangedIl(Type patch)
    {
        var (target, transpile) = Pin(patch);
        var original = PatchProcessor.GetOriginalInstructions(target);
        original.Insert(0, new CodeInstruction(OpCodes.Nop));

        Assert.Throws<InvalidOperationException>(() => transpile(original).ToList());
    }

    private static (System.Reflection.MethodBase, Func<System.Collections.Generic.List<CodeInstruction>, System.Collections.Generic.IEnumerable<CodeInstruction>>) Pin(Type patch) =>
        patch == typeof(PlayerMoney)
            ? (PlayerMoney.Target, PlayerMoney.Transpiler)
            : (PlayerMoneyFlush.Target, PlayerMoneyFlush.Transpiler);

    [Fact]
    public void PatchesInstall()
    {
        try
        {
            Assert.True(Patcher.Apply());
        }
        finally
        {
            new Harmony(Patcher.HarmonyId).UnpatchAll(Patcher.HarmonyId);
        }
    }

    [Fact]
    public void ConcurrentAddsLoseNothingAndDrainInIndexOrder()
    {
        var pending = new PlayerMoney.Pending();
        PlayerState a = NewPlayer(3), b = NewPlayer(1);

        Parallel.For(0, 100_000, i => pending.Add(i % 2 == 0 ? a : b, i % 3 == 0 ? -7 : 5));

        long Expected(int parity) => Enumerable.Range(0, 100_000).Where(i => i % 2 == parity).Sum(i => i % 3 == 0 ? -7L : 5L);
        Assert.Equal([(b, Expected(1)), (a, Expected(0))], pending.Drain());
        Assert.Empty(pending.Drain());
    }

    [Theory]
    [InlineData(100, 50L, 150)]
    [InlineData(-100, 2147483700L, 2147483600)] // one clamp of the exact total, not of a pre-clamped delta
    [InlineData(int.MaxValue, -1L, int.MaxValue - 1)]
    [InlineData(0, -3_000_000_000L, int.MinValue)]
    [InlineData(int.MinValue, 3_000_000_000L, 852516352)]
    public void ApplyClampsTheExactSumOnce(int money, long sum, int expected) =>
        Assert.Equal(expected, PlayerMoney.Apply(money, sum));
}
