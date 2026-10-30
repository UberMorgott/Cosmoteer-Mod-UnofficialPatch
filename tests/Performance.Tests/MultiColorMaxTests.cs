using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Cosmoteer.Data;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Graphics;
using Cosmoteer.Ships.Parts.Logic;
using Halfling.Graphics;
using Halfling.Geometry;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

public sealed class MultiColorMaxTests : IDisposable
{
    private const string HarmonyId = "performance.tests.multicolor-max";
    private static readonly Action<PartMultiColor, object?, EventArgs?> Changed =
        AccessTools.Method(typeof(PartMultiColor), "OnColorChanged").CreateDelegate<Action<PartMultiColor, object?, EventArgs?>>();

    public MultiColorMaxTests() => ApplyPatch();

    private static void ApplyPatch() => new Harmony(HarmonyId).Patch(MultiColorMax.Target,
        transpiler: new HarmonyMethod(AccessTools.Method(typeof(MultiColorMax), nameof(MultiColorMax.Transpiler))));

    public void Dispose() => new Harmony(HarmonyId).UnpatchAll(HarmonyId);

    [Fact]
    public void MaxCombinesEachChannelInsteadOfProducingNegativeInfinity()
    {
        Color color = Evaluate(MultiValueMode.Max, MultiValueMode.Max, false, false,
            new Color(0.2f, 0.8f, 0.3f, 0.4f), new Color(0.9f, 0.1f, 0.7f, 0.6f));
        Assert.Equal(new Color(0.9f, 0.8f, 0.7f, 0.6f), color);
    }

    [Fact]
    public void PreservesRgbAlphaAndInversionModes()
    {
        Color a = new(-2f, 4f, 0.5f, 0.25f), b = new(3f, -1f, 2f, 0.75f);
        foreach (MultiValueMode rgb in Enum.GetValues<MultiValueMode>())
            foreach (MultiValueMode alpha in Enum.GetValues<MultiValueMode>())
                foreach (bool invertRgb in new[] { false, true })
                    foreach (bool invertAlpha in new[] { false, true })
                    {
                        Vector3 expectedRgb = new(Combine(rgb, a.R, b.R), Combine(rgb, a.G, b.G), Combine(rgb, a.B, b.B));
                        float expectedAlpha = Combine(alpha, a.A, b.A);
                        if (invertRgb) expectedRgb = Vector3.One - expectedRgb;
                        if (invertAlpha) expectedAlpha = 1f - expectedAlpha;
                        Assert.Equal(new Color(expectedRgb.X, expectedRgb.Y, expectedRgb.Z, expectedAlpha), Evaluate(rgb, alpha, invertRgb, invertAlpha, a, b));
                    }
    }

    private static float Combine(MultiValueMode mode, float a, float b) => mode switch
    {
        MultiValueMode.Add => a + b,
        MultiValueMode.Multiply => a * b,
        MultiValueMode.Min => MathF.Min(a, b),
        MultiValueMode.Max => MathF.Max(a, b),
        MultiValueMode.Average => (a + b) / 2f,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    [Fact]
    public void MaxKeepsLibraryEdgeSemanticsAndEmptyIdentity()
    {
        float[] edges = [float.NegativeInfinity, -2f, -0f, 0f, 2f, float.PositiveInfinity, float.NaN];
        foreach (float a in edges)
            foreach (float b in edges)
            {
                Vector3 seed = new(float.NegativeInfinity), x = new(a), y = new(b);
                Vector3 first = Vector3.Max(in seed, in x);
                Vector3 expected = Vector3.Max(in first, in y);
                Color actual = Evaluate(MultiValueMode.Max, MultiValueMode.Max, false, false, new Color(a, a, a, 0.25f), new Color(b, b, b, 0.75f));
                Assert.Equal(expected.X, actual.R);
                Assert.Equal(expected.Y, actual.G);
                Assert.Equal(expected.Z, actual.B);
                Assert.Equal(0.75f, actual.A);
            }
        Assert.Equal(new Color(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity),
            Evaluate(MultiValueMode.Max, MultiValueMode.Max, false, false));
    }

    [Fact]
    public void BuffSourcesAndColorChangedKeepWorking()
    {
        PartMultiColor component = Create(MultiValueMode.Max, MultiValueMode.Max, false, false, [new Color(0.2f, 0.9f, 0.4f, 0.5f)], viaBuffs: true);
        var handler = (BuffMultiProxyHandler<IPartComponentColor>)AccessTools.Field(typeof(PartMultiColor), "_viaBuffs").GetValue(component)!;
        var list = (List<IPartComponentColor>)handler.ProxiedComponents;
        var source = new Source(new Color(0.2f, 0.3f, 0.4f, 0.5f));
        list.Add(source);
        AccessTools.Method(typeof(PartMultiColor), "OnViaBuffValueAdded").Invoke(component, [source]);
        int changed = 0;
        component.ColorChanged += (_, _) => changed++;
        source.Set(new Color(0.9f, 0.8f, 0.7f, 0.6f));
        Assert.Equal(new Color(0.9f, 0.9f, 0.7f, 0.6f), component.Color);
        Assert.Equal(1, changed);
        source.Set(source.Color);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void PinsParentAssociationAndChangesOnlyTheMinCall()
    {
        var original = PatchProcessor.GetOriginalInstructions(MultiColorMax.Target);
        var before = original.Select(c => c.ToString()).ToList();
        var patched = MultiColorMax.Transpiler(original).ToList();
        Assert.Equal(original.Count, patched.Count);
        Assert.Equal(1, before.Zip(patched).Count(pair => pair.First != pair.Second.ToString()));
        var changed = PatchProcessor.GetOriginalInstructions(MultiColorMax.Target);
        changed.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => MultiColorMax.Transpiler(changed).ToList());
        var parent = PatchProcessor.GetOriginalInstructions(MultiColorMax.Parent);
        parent.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => MultiColorMax.FindTarget(parent));
    }

    private static Color Evaluate(MultiValueMode rgb, MultiValueMode alpha, bool invertRgb, bool invertAlpha, params Color[] colors)
    {
        PartMultiColor component = Create(rgb, alpha, invertRgb, invertAlpha, colors);
        Changed(component, null, null);
        return component.Color;
    }

    private static PartMultiColor Create(MultiValueMode rgb, MultiValueMode alpha, bool invertRgb, bool invertAlpha, Color[] colors, bool viaBuffs = false)
    {
        // The rules constructor consults the running app; its color aggregation fields are independent of that state.
        var rules = (PartMultiColorRules)RuntimeHelpers.GetUninitializedObject(typeof(PartMultiColorRules));
        rules.Colors = new ID<PartComponentRules>[colors.Length];
        rules.RGBMode = rgb;
        rules.AMode = alpha;
        rules.InvertRGB = invertRgb;
        rules.InvertA = invertAlpha;
        if (viaBuffs)
        {
            rules.ViaBuffs = (BuffMultiProxyRules)RuntimeHelpers.GetUninitializedObject(typeof(BuffMultiProxyRules));
        }
        var component = new PartMultiColor(rules);
        var sources = (IPartComponentColor[])AccessTools.Field(typeof(PartMultiColor), "_colors").GetValue(component)!;
        for (int i = 0; i < sources.Length; i++)
        {
            sources[i] = new Source(colors[i]);
        }
        return component;
    }

    private sealed class Source(Color color) : IPartComponentColor
    {
        public Color Color { get; private set; } = color;
        public event EventHandler<EventArgs> ColorChanged = delegate { };
        public void Set(Color value)
        {
            Color = value;
            ColorChanged(this, EventArgs.Empty);
        }
    }
}
