using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Halfling.Geometry;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

public class MiniviewParticleBoxingTests
{
    [Fact]
    public void TranspilerMatchesRealGameIl()
    {
        var original = PatchProcessor.GetOriginalInstructions(MiniviewParticleBoxing.Target);
        var patched = MiniviewParticleBoxing.Transpiler(original).ToList();

        Assert.Equal(original.Count - 3, patched.Count);
        Assert.DoesNotContain(patched, i => i.opcode == OpCodes.Box);
        Assert.DoesNotContain(patched, i => i.operand is MethodInfo { Name: nameof(RotRect.IntersectsWith) } m && m.DeclaringType == typeof(RotRect));
        int call = patched.FindIndex(i => i.opcode == OpCodes.Call && i.operand is MethodInfo { Name: nameof(MiniviewParticleBoxing.Contains) });
        Assert.True(patched[call - 1].operand is MethodInfo { Name: "get_WorldLocation" });
        Assert.Equal(OpCodes.Ret, patched[call + 1].opcode);
        // Everything outside the replaced tail is untouched, labels and blocks included.
        Assert.Equal(original.Take(call).Select(Show), patched.Take(call).Select(Show));
        Assert.Equal(original.Skip(call + 4).Select(Show), patched.Skip(call + 1).Select(Show));
    }

    /// <summary>The IL contains ldc.r4 3.4E+38, whose listing is culture-formatted; the pin must hold for every player.</summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    [InlineData("sv-SE")]
    [InlineData("ar-SA")]
    public void IlPinIgnoresCulture(string name)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(name);
        try
        {
            _ = MiniviewParticleBoxing.Transpiler(PatchProcessor.GetOriginalInstructions(MiniviewParticleBoxing.Target)).ToList();
            Assert.Equal(name, CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void TranspilerRejectsChangedIl()
    {
        var original = PatchProcessor.GetOriginalInstructions(MiniviewParticleBoxing.Target);
        original.Insert(0, new CodeInstruction(OpCodes.Nop));

        Assert.Throws<InvalidOperationException>(() => MiniviewParticleBoxing.Transpiler(original).ToList());
    }

    /// <summary>Unboxed Contains gives the vanilla boxed IntersectsWith result, edges and non-finite values included.</summary>
    [Fact]
    public void ContainsMatchesBoxedIntersectsWith()
    {
        float[] special = [0f, -0f, 1f, -1f, 0.5f, 1e-7f, 3e38f, float.NaN, float.PositiveInfinity, float.NegativeInfinity];
        float[] rotations = [0f, MathF.PI / 2, MathF.PI, -MathF.PI / 4, 1e-6f, float.NaN];
        var rng = new Random(1234);
        float R() => (float)(rng.NextDouble() * 20 - 10);
        for (int n = 0; n < 4000; n++)
        {
            bool edge = n < 1000;
            var rect = new RotRect(edge ? special[n % special.Length] : R(), R(), edge ? special[n / 10 % special.Length] : Math.Abs(R()), Math.Abs(R()),
                new Direction(rotations[n % rotations.Length] + (edge ? 0f : R())));
            // Points on the corners and edges of the axis-aligned rect too.
            var point = (n % 4) switch
            {
                0 => new Vector2(rect.X, rect.Y),
                1 => new Vector2(rect.X + rect.Width, rect.Y + rect.Height),
                2 => new Vector2(special[n % special.Length], special[n / 7 % special.Length]),
                _ => new Vector2(R(), R()),
            };
            IShape boxed = point;
            Assert.Equal(rect.IntersectsWith(in boxed), MiniviewParticleBoxing.Contains(in rect, point));
        }
    }

    private static string Show(CodeInstruction ci) => $"{ci} [{ci.labels.Count}] [{string.Join(",", ci.blocks.Select(b => b.blockType))}]";
}
