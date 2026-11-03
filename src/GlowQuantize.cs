using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Cosmoteer.Ships.Networks;
using Cosmoteer.Ships.Parts.Graphics;
using Cosmoteer.Ships.Parts.Logic;
using Halfling.Graphics;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>
/// Heat glow colours (<see cref="PartValueColor"/> fed by network-average thermal values) change by tiny amounts every
/// tick, and each change makes every consumer rewrite its ship quads (~19k colour writes per frame with a moving fleet).
/// A change that moves no channel by a full 8-bit step (1/255) is not announced: the cached colour stays what every
/// consumer last read, so all readers keep agreeing and each source channel stays within 1/255 of the exact value
/// (an approved small visual approximation; blended pixels where glows overlap may differ slightly more).
/// Only <c>ColorChanged</c> consumers see the effect, all render/effect code (PartGraphics, sprite components,
/// PartMultiColor, PartContinuousEffects); no sim code reads it. Off: <c>UNOFFICIAL_PATCH_GLOW_QUANTIZE=0</c>.
/// <para/>
/// Deciding needs the new colour during the source's <c>ValueChanged</c> event (which can fire inside the sim), while
/// vanilla only reads it later on draw. So only colours whose every channel provider is an exact
/// <see cref="NativeProviders"/> type are quantized: their <c>Value</c> getters are audited plain backing-field reads
/// (checked against the installed IL), so evaluating them early has no side effect and reads nothing upstream. Any
/// other provider (custom or modded type, subclass, lambda provider) keeps the vanilla handler for that colour.
/// <para/>
/// A changed game body rolls back every patch; a foreign Harmony owner on a dependency, including the audited getters,
/// announces every change again (checked every frame with the other features). <see cref="StayRegistered"/> keeps the
/// fewer announcements from turning into BeforeDraw unsubscribe/resubscribe garbage.
/// </summary>
internal static class GlowQuantize
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("UNOFFICIAL_PATCH_GLOW_QUANTIZE"));
    internal static bool IsRequested(string? value) => value != "0";

    internal const float Step = 1f / 255f;
    private const string IlSha256 = "61150B1A9336B7D6EBCBB6D6F81E297DF97BAC382B1485E09206D633870775D8";
    private const string UpdateColorIlSha256 = "7A7CEDA0FC716ED34DCE973BFB27767E1089DD1E51C48BAB477F88732BF35F4D";

    internal static readonly MethodInfo Target = AccessTools.Method(typeof(PartValueColor), "OnValueChanged")
        ?? throw new MissingMethodException(nameof(PartValueColor), "OnValueChanged");
    internal static readonly MethodInfo UpdateColor = AccessTools.Method(typeof(PartValueColor), "UpdateColor")
        ?? throw new MissingMethodException(nameof(PartValueColor), "UpdateColor");

    /// <summary>
    /// Provider types the vanilla glows use (heat pipes/exchangers: NetworkValueReceiver; radiators: MultiValue over a
    /// SmoothedValue and a StaticValue; pumps: ValueAccumulator). Matched by exact type, never by subclass.
    /// </summary>
    internal static readonly Type[] AllowedProviders =
        [typeof(PartNetworkValueReceiver), typeof(PartMultiValue), typeof(PartSmoothedValue), typeof(PartValueAccumulator), typeof(PartStaticValue)];

    internal static readonly MethodInfo[] ProviderGetters = AllowedProviders
        .Select(t => AccessTools.PropertyGetter(t, nameof(IComponentValueProvider.Value)) ?? throw new MissingMethodException(t.Name, "get_Value"))
        .ToArray();

    /// <summary>The allowed types whose installed getter is still exactly <c>ldarg.0; ldfld own field; ret</c>.</summary>
    internal static readonly Type[] NativeProviders = AllowedProviders.Where((_, i) => IsFieldRead(ProviderGetters[i])).ToArray();

    /// <summary>
    /// The patched handlers, the colour computation Skip reuses, the getter consumers read and the provider getters
    /// Skip evaluates early.
    /// </summary>
    internal static readonly MethodBase[] Dependencies =
        [Target, UpdateColor, AccessTools.PropertyGetter(typeof(PartValueColor), nameof(PartValueColor.Color)), StayRegistered.GraphicsUpdateColor, .. ProviderGetters];

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? [(Target, typeof(GlowQuantize)), (StayRegistered.GraphicsUpdateColor, typeof(StayRegistered))] : [];

    private static int _ready;
    private static string? _foreign, _loggedForeign;
    internal static bool Ready => Volatile.Read(ref _ready) != 0;
    internal static bool Active => Ready && Volatile.Read(ref _foreign) == null;

    /// <summary>Prepends <c>if (GlowQuantize.Skip(this)) return;</c>.</summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        // Skip relies on UpdateColor writing _cachedColor and clearing _needsRefresh, as pinned here.
        Patcher.RequireIl(PatchProcessor.GetOriginalInstructions(UpdateColor), UpdateColorIlSha256, "PartValueColor.UpdateColor");
        List<CodeInstruction> code = Patcher.RequireIl(instructions, IlSha256, "PartValueColor.OnValueChanged");
        Label body = generator.DefineLabel();
        code[0].labels.Add(body);
        code.InsertRange(0,
        [
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GlowQuantize), nameof(Skip), [typeof(PartValueColor)])),
            new CodeInstruction(OpCodes.Brfalse, body),
            new CodeInstruction(OpCodes.Ret),
        ]);
        return code;
    }

    /// <summary>Throws when the hook is missing (rolls back every patch).</summary>
    internal static bool Initialize()
    {
        if (!Requested) return true;
        Patches? patches = Harmony.GetPatchInfo(Target);
        if (patches == null || !patches.Transpilers.Any(p => p.owner == Patcher.HarmonyId && p.PatchMethod.DeclaringType == typeof(GlowQuantize)))
            throw new InvalidOperationException("Glow quantize hook missing on PartValueColor.OnValueChanged");
        SetForeign(ScanForeign());
        Volatile.Write(ref _ready, 1);
        Log.Info("Glow colour quantize ready, native providers: " + string.Join(", ", NativeProviders.Select(t => t.Name))
            + (NativeProviders.Length == AllowedProviders.Length ? "" : " (changed getters run vanilla: "
                + string.Join(", ", AllowedProviders.Except(NativeProviders).Select(t => t.Name)) + ")"));
        return true;
    }

    internal static bool IsDependency(MethodBase method)
    {
        foreach (MethodBase dependency in Dependencies)
            if (BlueprintToggleEvaluation.SameMethodDefinition(dependency, method)) return true;
        return false;
    }

    /// <summary>Per-frame result of the shared foreign-owner scan (<see cref="BlueprintToggleEvaluation"/>); null = none.</summary>
    internal static void SetForeign(string? reason)
    {
        Volatile.Write(ref _foreign, reason);
        if (reason != null && Interlocked.Exchange(ref _loggedForeign, reason) == null)
            Log.Info("Glow colour quantize paused (every change announced): " + reason);
    }

    private static string? ScanForeign()
    {
        foreach (MethodBase method in Dependencies)
        {
            Patches? patches = Harmony.GetPatchInfo(method);
            if (patches == null) continue;
            foreach (string owner in patches.Owners)
                if (owner != Patcher.HarmonyId) return "foreign Harmony owner " + owner + " on " + method.DeclaringType?.Name + "." + method.Name;
        }
        return null;
    }

    internal static void Disable() => Volatile.Write(ref _ready, 0);

    /// <summary>True when the getter's original IL is a plain read of a field of its own type.</summary>
    internal static bool IsFieldRead(MethodInfo getter)
    {
        List<CodeInstruction> il = PatchProcessor.GetOriginalInstructions(getter);
        return il.Count == 3 && il[0].opcode == OpCodes.Ldarg_0 && il[1].opcode == OpCodes.Ldfld
            && il[1].operand is FieldInfo { IsStatic: false } field && field.DeclaringType == getter.DeclaringType
            && il[2].opcode == OpCodes.Ret;
    }

    /// <summary>
    /// Every channel is unused or comes from an exact <see cref="NativeProviders"/> type. Checked on every event, not
    /// cached: the providers are reassigned when the component is re-added to a part, and a few type-handle compares
    /// cost less than any per-instance cache lookup.
    /// </summary>
    internal static bool Eligible(PartValueColor color) =>
        Native(color._redValue) && Native(color._greenValue) && Native(color._blueValue) && Native(color._alphaValue);

    private static bool Native(IComponentValueProvider? provider)
    {
        if (provider == null) return true;
        Type type = provider.GetType();
        foreach (Type native in NativeProviders)
            if (type == native) return true;
        return false;
    }

    /// <summary>
    /// True when the new colour is <see cref="Quiet(Color, Color)"/> against the cached (last read) one: the cache is kept and no event
    /// is raised. A pending refresh (first colour after attach, or a change not read yet) and a colour with a provider
    /// outside <see cref="NativeProviders"/> always run vanilla.
    /// </summary>
    internal static bool Skip(PartValueColor color)
    {
        if (!Active || color._needsRefresh || !Eligible(color)) return false;
        Color shown = color._cachedColor;
        color.UpdateColor();
        if (!Quiet(shown, color._cachedColor)) return false;
        color._cachedColor = shown;
        return true;
    }

    /// <summary>
    /// Settling rule: a change is announced when any channel moves by at least one 8-bit step from the shown colour, or
    /// lands exactly on 0 or 1 (glow fully off or fully on) while the shown value differs. So a value that settles at an
    /// endpoint is always shown exactly, and a value resting anywhere else is shown within 1/255. NaN or infinite
    /// differences are never quiet.
    /// </summary>
    internal static bool Quiet(Color shown, Color next) =>
        Quiet(shown.R, next.R) && Quiet(shown.G, next.G) && Quiet(shown.B, next.B) && Quiet(shown.A, next.A);

    private static bool Quiet(float shown, float next) =>
        MathF.Abs(next - shown) < Step && (next.Equals(shown) || next is not (0f or 1f));

    /// <summary>
    /// PartGraphics leaves BeforeDraw after one frame without a colour change and joins again on the next change. Vanilla
    /// glow colours change every frame, so they never leave; quantized ones would leave and rejoin every few frames, and
    /// each leave copies the ship's whole BeforeDraw invocation list (~100 MB/s of garbage with 40 ships). While the
    /// feature is active, a part coloured by a <see cref="PartValueColor"/> stays subscribed as in vanilla; the handler
    /// returns early on frames without a change. Detaching still unsubscribes (PartGraphics.OnPartDetaching).
    /// Staying subscribed also keeps the part's place in the BeforeDraw invocation order across quiet periods, which is
    /// the order vanilla has for an always-changing glow; leaving and rejoining would move it to the end of the list.
    /// </summary>
    internal static class StayRegistered
    {
        private const string GraphicsIlSha256 = "BF9FBEEFAF23D1DB434B098A0131596BEE5C64D1C07D8DE6A2BC919B8B10D720";

        internal static readonly MethodInfo GraphicsUpdateColor = AccessTools.Method(typeof(PartGraphics), "UpdateColor")
            ?? throw new MissingMethodException(nameof(PartGraphics), "UpdateColor");

        // Harmony binds the hook by the name Transpiler.
        // ReSharper disable once MemberHidesStaticFromOuterClass
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            List<CodeInstruction> code = Patcher.RequireIl(instructions, GraphicsIlSha256, "PartGraphics.UpdateColor");
            // [0..7) if (HasFlag(Dirty)) goto update; then the unsubscribe branch. Prepend to it: if (Keep(this)) return;
            Label unsubscribe = generator.DefineLabel();
            code[7].labels.Add(unsubscribe);
            code.InsertRange(7,
            [
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(StayRegistered), nameof(Keep))),
                new CodeInstruction(OpCodes.Brfalse, unsubscribe),
                new CodeInstruction(OpCodes.Ret),
            ]);
            return code;
        }

        /// <summary>Only colours <see cref="Skip(PartValueColor)"/> can quantize stay; any other source leaves as in vanilla.</summary>
        internal static bool Keep(PartGraphics graphics) => Active && graphics._getColorFrom is PartValueColor color && Eligible(color);
    }
}
