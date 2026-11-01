using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Networks;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Graphics;
using Cosmoteer.Ships.Parts.Logic;
using Cosmoteer.Ships.Rendering;
using Halfling.Graphics;
using Halfling.Scene2D;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

public sealed class GlowQuantizeTests
{
    private const string ForeignId = "performance.tests.foreign-glow";

    [Fact]
    public void KillSwitchIsOnlyZero()
    {
        Assert.False(GlowQuantize.IsRequested("0"));
        Assert.True(GlowQuantize.IsRequested(null));
        Assert.True(GlowQuantize.IsRequested("1"));
    }

    [Fact]
    public void QuietMeansEveryChannelBelowOneStepAndNoEndpointReached()
    {
        Color a = new(0.5f, 0.5f, 0.5f, 0.5f);
        float below = GlowQuantize.Step * 0.99f;
        Assert.True(GlowQuantize.Quiet(a, a));
        Assert.True(GlowQuantize.Quiet(a, new Color(0.5f + below, 0.5f - below, 0.5f + below, 0.5f - below)));
        Assert.False(GlowQuantize.Quiet(a, new Color(0.5f + 1.01f * GlowQuantize.Step, 0.5f, 0.5f, 0.5f)));
        Assert.False(GlowQuantize.Quiet(a, new Color(0.5f, 0.5f, 0.5f, 0.5f - 1.01f * GlowQuantize.Step)));
        Assert.False(GlowQuantize.Quiet(a, new Color(float.NaN, 0.5f, 0.5f, 0.5f)));
        Assert.False(GlowQuantize.Quiet(new Color(float.PositiveInfinity, 0, 0, 0), new Color(float.PositiveInfinity, 0, 0, 0)));
        // Landing exactly on 0 or 1 is always shown; staying there is quiet.
        Assert.False(GlowQuantize.Quiet(new Color(1, 1, 1, below), new Color(1, 1, 1, 0)));
        Assert.False(GlowQuantize.Quiet(new Color(1, 1, 1, 1 - below), new Color(1, 1, 1, 1)));
        Assert.True(GlowQuantize.Quiet(new Color(1, 1, 1, 0), new Color(1, 1, 1, 0)));
        Assert.True(GlowQuantize.Quiet(new Color(1, 1, 1, 0), new Color(1, 1, 1, below)));
    }

    [Fact]
    public void AllowedProviderGettersAreAuditedFieldReads()
    {
        Assert.Equal(GlowQuantize.AllowedProviders, GlowQuantize.NativeProviders);
        Assert.False(GlowQuantize.IsFieldRead(AccessTools.PropertyGetter(typeof(PartValueColor), nameof(PartValueColor.Color))));
    }

    [Fact]
    public void DependenciesCoverHandlerComputationGetterAndProviderGetters()
    {
        Assert.True(GlowQuantize.IsDependency(GlowQuantize.Target));
        Assert.True(GlowQuantize.IsDependency(GlowQuantize.UpdateColor));
        Assert.True(GlowQuantize.IsDependency(AccessTools.PropertyGetter(typeof(PartValueColor), nameof(PartValueColor.Color))));
        Assert.True(GlowQuantize.IsDependency(GlowQuantize.StayRegistered.GraphicsUpdateColor));
        foreach (Type provider in GlowQuantize.AllowedProviders)
            Assert.True(GlowQuantize.IsDependency(AccessTools.PropertyGetter(provider, nameof(IComponentValueProvider.Value))), provider.Name);
        Assert.False(GlowQuantize.IsDependency(AccessTools.Method(typeof(PartMultiColor), "OnColorChanged")));
    }

    [Fact]
    public void PrependsOnlyTheSkipGuardAndRejectsChangedGameIl()
    {
        var original = PatchProcessor.GetOriginalInstructions(GlowQuantize.Target);
        var patched = GlowQuantize.Transpiler(original, new DynamicMethod("x", null, null).GetILGenerator()).ToList();
        Assert.Equal(original.Count + 4, patched.Count);
        Assert.True(patched[1].Calls(AccessTools.Method(typeof(GlowQuantize), nameof(GlowQuantize.Skip), [typeof(PartValueColor)])));
        Assert.Equal(OpCodes.Ret, patched[3].opcode);
        Assert.Contains((Label)patched[2].operand, patched[4].labels);
        var changed = PatchProcessor.GetOriginalInstructions(GlowQuantize.Target);
        changed.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => GlowQuantize.Transpiler(changed, new DynamicMethod("y", null, null).GetILGenerator()).ToList());
    }

    [Fact]
    public void StayRegisteredOnlyGuardsTheUnsubscribeBranch()
    {
        var original = PatchProcessor.GetOriginalInstructions(GlowQuantize.StayRegistered.GraphicsUpdateColor);
        var patched = GlowQuantize.StayRegistered.Transpiler(original, new DynamicMethod("z", null, null).GetILGenerator()).ToList();
        Assert.Equal(original.Count + 4, patched.Count);
        int keep = patched.FindIndex(c => c.Calls(AccessTools.Method(typeof(GlowQuantize.StayRegistered), nameof(GlowQuantize.StayRegistered.Keep))));
        int remove = patched.FindIndex(c => c.operand is MethodInfo { Name: "remove_BeforeDraw" });
        Assert.InRange(keep, 1, remove - 1);
        Assert.Equal(OpCodes.Ret, patched[keep + 2].opcode);
        var changed = PatchProcessor.GetOriginalInstructions(GlowQuantize.StayRegistered.GraphicsUpdateColor);
        changed.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => GlowQuantize.StayRegistered.Transpiler(changed, new DynamicMethod("w", null, null).GetILGenerator()).ToList());
    }

    [Fact]
    public void PatchedHandlerHoldsSubStepChangesOfANativeProvider() => Patched(() =>
    {
        var (receiver, color, events) = Native();
        Assert.Equal(1, Change(receiver, 0.5f, color, events)); // pending first refresh: always announced
        Assert.Equal(0.5f, color.Color.A);
        float half = GlowQuantize.Step / 2f;
        Assert.Equal(1, Change(receiver, 0.5f + half, color, events));
        Assert.Equal(0.5f, color.Color.A); // consumers keep reading what they last applied
        Assert.False(color._needsRefresh);
        Assert.Equal(1, Change(receiver, 0.5f + 1.5f * half, color, events));
        Assert.Equal(0.5f, color.Color.A);
        Assert.Equal(2, Change(receiver, 0.5f + 2.5f * half, color, events)); // drift is measured from the shown colour
        Assert.Equal(0.5f + 2.5f * half, color.Color.A);
        // An unread change is pending: the next one is vanilla even below a step.
        receiver.Value = 0.6f;
        receiver.Value = 0.6f + half;
        Assert.Equal(4, events());
        Assert.Equal(0.6f + half, color.Color.A);
    });

    [Fact]
    public void PatchedHandlerSettlesExactlyOnEndpoints() => Patched(() =>
    {
        var (receiver, color, events) = Native();
        float tiny = GlowQuantize.Step / 4f;
        Assert.Equal(1, Change(receiver, tiny, color, events));
        Assert.Equal(2, Change(receiver, 0f, color, events)); // residual below one step, but fully off is shown exactly
        Assert.Equal(0f, color.Color.A);
        Assert.Equal(2, Change(receiver, tiny, color, events)); // leaving 0 by less than a step stays quiet
        Assert.Equal(0f, color.Color.A);
        Assert.Equal(3, Change(receiver, 1f - tiny, color, events));
        Assert.Equal(4, Change(receiver, 1f, color, events));
        Assert.Equal(1f, color.Color.A);
    });

    [Fact]
    public void PatchedHandlerRunsVanillaForProvidersOutsideTheAllowlist() => Patched(() =>
    {
        var custom = new Source(0.5f);
        var color = Color(custom);
        int count = 0;
        color.ColorChanged += (_, _) => count++;
        custom.Set(0.5f);
        _ = color.Color;
        Assert.False(GlowQuantize.Eligible(color));
        custom.Set(0.5f + GlowQuantize.Step / 4f);
        Assert.Equal(2, count);
        Assert.Equal(0.5f + GlowQuantize.Step / 4f, color.Color.A);
        // One foreign channel is enough to keep the whole colour vanilla.
        var (receiver, mixed, events) = Native();
        mixed._redValue = custom;
        Assert.False(GlowQuantize.Eligible(mixed));
        Assert.Equal(1, Change(receiver, 0.5f, mixed, events));
        Assert.Equal(2, Change(receiver, 0.5f + GlowQuantize.Step / 4f, mixed, events));
    });

    [Fact]
    public void ForeignPatchOnAProviderGetterPausesUntilItIsRemoved() => Patched(() =>
    {
        var foreign = new Harmony(ForeignId);
        try
        {
            var (receiver, color, events) = Native();
            float quarter = GlowQuantize.Step / 4f;
            Assert.Equal(1, Change(receiver, 0.5f, color, events));
            foreign.Patch(AccessTools.PropertyGetter(typeof(PartNetworkValueReceiver), nameof(PartNetworkValueReceiver.Value)),
                prefix: new HarmonyMethod(typeof(GlowQuantizeTests), nameof(ForeignPrefix)));
            BlueprintToggleEvaluation.BeginValidatedUpdate();
            BlueprintToggleEvaluation.EndValidatedUpdate();
            Assert.False(GlowQuantize.Active);
            Assert.Equal(2, Change(receiver, 0.5f + quarter, color, events)); // paused: every change announced
            foreign.UnpatchAll(ForeignId);
            BlueprintToggleEvaluation.BeginValidatedUpdate();
            BlueprintToggleEvaluation.EndValidatedUpdate();
            Assert.True(GlowQuantize.Active);
            Assert.Equal(2, Change(receiver, 0.5f + 2 * quarter, color, events)); // resumed
        }
        finally { foreign.UnpatchAll(ForeignId); }
    });

    [Fact]
    public void PatchedGraphicsStaysSubscribedWhileQuietAndDetachUnsubscribes() => Patched(() =>
    {
        var (receiver, color, _) = Native();
        receiver.Value = 0.5f;
        var (graphics, renderer) = Graphics(color);
        graphics.OnColorChanged(null, EventArgs.Empty);
        Assert.True(Subscribed(renderer, graphics));
        BeforeDraw(renderer); // dirty frame: colour applied
        Assert.False(graphics._colorUpdateStatus.HasFlag(PartGraphics.ColorUpdateFlags.Dirty));
        BeforeDraw(renderer); // quiet frame: vanilla would leave BeforeDraw here
        BeforeDraw(renderer);
        Assert.True(Subscribed(renderer, graphics));
        Assert.True(graphics._colorUpdateStatus.HasFlag(PartGraphics.ColorUpdateFlags.Registered));
        graphics.OnPartDetaching();
        Assert.False(Subscribed(renderer, graphics));
        Assert.False(graphics._colorUpdateStatus.HasFlag(PartGraphics.ColorUpdateFlags.Registered));
    });

    [Fact]
    public void PatchedGraphicsLeavesForAnIneligibleValueColour() => Patched(() =>
    {
        var custom = new Source(0.25f);
        var color = Color(custom);
        Assert.False(GlowQuantize.Eligible(color));
        var (graphics, renderer) = Graphics(color);
        graphics.OnColorChanged(null, EventArgs.Empty);
        Assert.True(Subscribed(renderer, graphics));
        BeforeDraw(renderer); // dirty frame: colour applied (read from the source)
        Assert.False(color._needsRefresh);
        Assert.Equal(0.25f, color.Color.A);
        Assert.False(graphics._colorUpdateStatus.HasFlag(PartGraphics.ColorUpdateFlags.Dirty));
        Assert.True(Subscribed(renderer, graphics));
        BeforeDraw(renderer); // clean frame: vanilla unsubscribe
        Assert.False(Subscribed(renderer, graphics));
        Assert.False(graphics._colorUpdateStatus.HasFlag(PartGraphics.ColorUpdateFlags.Registered));
    });

    [Fact]
    public void PatchedGraphicsLeavesWhenInactiveOrNotAValueColour() => Patched(() =>
    {
        var (receiver, color, _) = Native();
        receiver.Value = 0.5f;
        var (graphics, renderer) = Graphics(color);
        graphics.OnColorChanged(null, EventArgs.Empty);
        BeforeDraw(renderer);
        GlowQuantize.SetForeign("test");
        BeforeDraw(renderer);
        Assert.False(Subscribed(renderer, graphics));
        GlowQuantize.SetForeign(null);
        var (other, otherRenderer) = Graphics(new PartMultiColor((PartMultiColorRules)RuntimeHelpers.GetUninitializedObject(typeof(PartMultiColorRules))));
        other._colorUpdateStatus = PartGraphics.ColorUpdateFlags.Registered;
        otherRenderer.BeforeDraw += other.UpdateColor;
        BeforeDraw(otherRenderer);
        Assert.False(Subscribed(otherRenderer, other));
    });

    private static void ForeignPrefix() { }

    /// <summary>Applies the mod's real Harmony patches for this feature, as <see cref="Patcher"/> does, then initializes it.</summary>
    private static void Patched(Action body)
    {
        var own = new Harmony(Patcher.HarmonyId);
        try
        {
            foreach ((MethodBase target, Type patch) in GlowQuantize.Patches)
                own.Patch(target, transpiler: new HarmonyMethod(patch.GetMethod("Transpiler", BindingFlags.Public | BindingFlags.Static)));
            Assert.True(GlowQuantize.Initialize());
            Assert.True(GlowQuantize.Active);
            body();
        }
        finally
        {
            GlowQuantize.Disable();
            GlowQuantize.SetForeign(null);
            foreach ((MethodBase target, _) in GlowQuantize.Patches) own.Unpatch(target, HarmonyPatchType.All, own.Id);
        }
    }

    /// <summary>Changes the value, reads the colour as a consumer does on its next draw, and returns the event count.</summary>
    private static int Change(PartNetworkValueReceiver value, float next, PartValueColor color, Func<int> events)
    {
        value.Value = next;
        _ = color.Color;
        return events();
    }

    /// <summary>A glow as the game data builds it: alpha from a network value receiver, subscribed as in OnAddedToPart.</summary>
    private static (PartNetworkValueReceiver, PartValueColor, Func<int>) Native()
    {
        var receiver = (PartNetworkValueReceiver)RuntimeHelpers.GetUninitializedObject(typeof(PartNetworkValueReceiver));
        var color = Color(receiver);
        int count = 0;
        color.ColorChanged += (_, _) => count++;
        return (receiver, color, () => count);
    }

    private static PartValueColor Color(IComponentValueProvider alpha)
    {
        var rules = (PartValueColorRules)RuntimeHelpers.GetUninitializedObject(typeof(PartValueColorRules));
        rules.Default = Halfling.Graphics.Color.White;
        var color = new PartValueColor(rules) { _alphaValue = alpha, _needsRefresh = true };
        alpha.ValueChanged += color.OnValueChanged; // the patched handler
        return color;
    }

    /// <summary>A PartGraphics coloured by <paramref name="source"/> on a part of a ship with a real BeforeDraw event.</summary>
    private static (PartGraphics, ShipRenderer) Graphics(IPartComponentColor source)
    {
        var renderer = (ShipRenderer)RuntimeHelpers.GetUninitializedObject(typeof(ShipRenderer));
        renderer._drawFields = new SceneComponent.DrawFields(false); // as the SceneComponent constructor does for a Draw override
        var ship = (Ship)RuntimeHelpers.GetUninitializedObject(typeof(Ship));
        Set(ship, "<Renderer>k__BackingField", renderer);
        var part = (Part)RuntimeHelpers.GetUninitializedObject(typeof(Part));
        part.p_ship = ship;
        var graphics = (PartGraphics)RuntimeHelpers.GetUninitializedObject(typeof(PartGraphics));
        Set(graphics, "<Part>k__BackingField", part);
        graphics._getColorFrom = source;
        return (graphics, renderer);
    }

    private static void Set(object target, string field, object value)
    {
        for (Type? type = target.GetType(); type != null; type = type.BaseType)
            if (type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly) is { } f)
            {
                f.SetValue(target, value);
                return;
            }
        throw new MissingFieldException(target.GetType().Name, field);
    }

    private static EventHandler<DrawEventArgs>? Handlers(ShipRenderer renderer) =>
        renderer._events.Get<EventHandler<DrawEventArgs>>(SceneComponent.E_BEFORE_DRAW);

    private static void BeforeDraw(ShipRenderer renderer) => Handlers(renderer)?.Invoke(renderer, null!);

    private static bool Subscribed(ShipRenderer renderer, PartGraphics graphics) =>
        Handlers(renderer)?.GetInvocationList().Any(d => d.Target == graphics) == true;

    private sealed class Source(float value) : IComponentValueProvider
    {
        public float Value { get; private set; } = value;
        public event EventHandler<EventArgs> ValueChanged = delegate { };
        public void Set(float next)
        {
            Value = next;
            ValueChanged(this, EventArgs.Empty);
        }
    }
}
