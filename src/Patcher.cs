using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Installs every patch, all-or-nothing; any failure rolls back all of this mod's patches and the game runs vanilla.
///
/// Inlining: Cosmoteer.dll is ReadyToRun. Harmony detours a patched method's entry point, but precompiled callers that
/// already contain an inlined copy of it would keep the vanilla body. Those are read exactly from the image's R2R inlining
/// table (<see cref="R2RInlineInfo"/>) and re-patched with <see cref="Rejit"/>, which rebuilds them from IL so they call
/// the patched method. Shared generic code is never patched; if the table lists a generic inliner, loading is refused.
///
/// Performance patches preserve sim results and native callback behavior. Desync fixes (<see cref="PlayerMoney"/>, <see cref="MissionTrade"/>, <see cref="ForeignJobArrival"/>) change them, so
/// every MP player must run the same mod version.
/// </summary>
internal static class Patcher
{
    public const string HarmonyId = "morgott.performance";

    /// <summary>Each target and its public static Prefix / Postfix / Transpiler / Finalizer (any subset).</summary>
    internal static (MethodBase Target, Type Patch)[] Patches =>
    [
        .. PortToggleMode.Targets.Select(t => ((MethodBase)t.Target, typeof(PortToggleMode))),
        (BlueprintEvaluationScope.Target, typeof(BlueprintEvaluationScope)),
        .. BlueprintToggleUpdates.Patches,
        .. SmoothedActiveSet.Patches,
        .. AtlasRangeUpload.Patches,
        .. ParallelIdleParking.Patches,
        .. CullCadence.Patches,
        .. RaycastScratch.Patches,
        .. BeamTargetMemo.Patches,
        .. BeamParallel.Patches,
        .. BeamFrozen.Patches,
        (ThrusterAccelerationCache.Target, typeof(ThrusterAccelerationCache)),
        (MapNodeText.Target, typeof(MapNodeText)),
        (MusicSeek.Target, typeof(MusicSeek)),
        (MiniviewParticleBoxing.Target, typeof(MiniviewParticleBoxing)),
        .. ShaderConstantBoxing.Targets.Select(t => ((MethodBase)t, typeof(ShaderConstantBoxing))),
        (RoofOpacity.Target, typeof(RoofOpacity)),
        (MultiColorMax.Target, typeof(MultiColorMax)),
        .. GlowQuantize.Patches,
        .. NebulaTempList.Patches,
        .. StatusTempDispose.Patches,
        .. BoundingPoints.Patches,
        .. HullRemovalSkip.Patches,
        .. DecalLookup.Patches,
        .. SceneBucketRemove.Patches,
        .. SpCatchUp.Patches,
        .. TickBudget.Patches,
        (PlayerMoney.Target, typeof(PlayerMoney)),
        (PlayerMoneyFlush.Target, typeof(PlayerMoneyFlush)),
        .. MissionTrade.Targets.Select(t => ((MethodBase)t.Target, typeof(MissionTrade))),
        (ForeignJobArrival.Target, typeof(ForeignJobArrival)),
        (InputArrivalTelemetry.Target, typeof(InputArrivalTelemetry)),
        (StallTelemetry.Target, typeof(StallTelemetry)),
        (ServerGc.Target, typeof(ServerGc)),
        .. ViewDetect.Patches,
    ];

    /// <summary>True only when every patch is installed.</summary>
    public static bool Apply()
    {
        var sw = Stopwatch.StartNew();
        var harmony = new Harmony(HarmonyId);
        string? retargetError = null;
        try
        {
            // Shared generic targets only stay patched after tier-up with the retarget listener; without it they stay vanilla.
            List<MethodBase> shared = Patches.Select(p => p.Target).Where(SharedGenericRetarget.IsShared).Distinct().ToList();
            bool retarget = shared.Count == 0 || SharedGenericRetarget.Enable(shared, out retargetError);
            if (!retarget) Log.Info($"{retargetError}; shared generic patches inactive: " + string.Join(", ", shared.Select(t => t.DeclaringType?.Name + "." + t.Name)));
            var patches = Patches.Where(p => retarget || !SharedGenericRetarget.IsShared(p.Target))
                .Select(p => (p.Target, p.Patch, Inliners: FindR2RInliners(p.Target))).ToList(); // pure read, before anything is patched
            // Before BlueprintToggleUpdates' assembly watcher exists: the guard assembly is then known by instance.
            ViewDetect.Prepare();
            var rejit = new HarmonyMethod(AccessTools.Method(typeof(Patcher), nameof(Rejit)));
            int inlinerCount = 0;
            foreach ((MethodBase target, Type patch, List<MethodBase> inliners) in patches)
            {
                harmony.Patch(target, Hook(patch, "Prefix"), Hook(patch, "Postfix"), Hook(patch, "Transpiler"), Hook(patch, "Finalizer"));
                foreach (MethodBase inliner in inliners)
                {
                    harmony.Patch(inliner, transpiler: rejit);
                    Log.Info($"  R2R inliner of {target.Name} patched: {inliner.DeclaringType?.FullName}.{inliner.Name}");
                }
                inlinerCount += inliners.Count;
            }
            // Throws (rollback) when the game changed; false only keeps this one feature native (foreign code).
            if (BlueprintToggleUpdates.Requested) _ = BlueprintToggleUpdates.Initialize();
            if (SmoothedActiveSet.Requested) _ = SmoothedActiveSet.Initialize();
            if (AtlasRangeUpload.Requested) _ = AtlasRangeUpload.Initialize();
            if (ParallelIdleParking.Requested) _ = ParallelIdleParking.Initialize();
            if (CullCadence.Requested) _ = CullCadence.Initialize();
            if (RaycastScratch.Requested) _ = RaycastScratch.Initialize();
            if (BeamTargetMemo.Requested) _ = BeamTargetMemo.Initialize();
            if (BeamParallel.Requested) _ = BeamParallel.Initialize();
            if (BeamFrozen.Requested) _ = BeamFrozen.Initialize();
            if (GlowQuantize.Requested) _ = GlowQuantize.Initialize();
            if (SceneBucketRemove.Requested) SceneBucketRemove.Initialize();
            // Diagnostic getter guards: installed apart from Patches (no R2R inliner rejit, skip instead of rollback).
            if (ViewDetect.Requested) _ = ViewDetect.Initialize(harmony);
            Log.Info($"Patched {patches.Count} methods + {inlinerCount} R2R inliners in {sw.ElapsedMilliseconds} ms");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("patch", ex);
            RollBack(() => harmony.UnpatchAll(HarmonyId));
            Log.Info("Performance DISABLED: patches rolled back, game runs vanilla.");
            return false;
        }
    }

    private static HarmonyMethod? Hook(Type patch, string name) =>
        patch.GetMethod(name, BindingFlags.Public | BindingFlags.Static) is { } m ? new HarmonyMethod(m) : null;

    /// <summary>Undoes every patch of this mod; throws if any is still installed afterwards (game is then not vanilla).</summary>
    internal static void RollBack(Action unpatchAll)
    {
        BlueprintToggleEvaluation.EndValidatedUpdate();
        BlueprintToggleUpdates.Disable();
        SmoothedActiveSet.Disable();
        BeamTargetMemo.Disable();
        BeamParallel.Disable();
        BeamFrozen.Disable();
        RaycastScratch.Disable();
        CullCadence.Disable();
        ParallelIdleParking.Disable();
        AtlasRangeUpload.Disable();
        GlowQuantize.Disable();
        SceneBucketRemove.Disable();
        ViewDetect.Disable();
        // Before unpatching: no compile callback may forward a detour that is being removed.
        if (!SharedGenericRetarget.Disable()) Log.Info("Performance: shared generic retarget listener could not be removed (inert).");
        try
        {
            unpatchAll();
        }
        catch (Exception ex)
        {
            Log.Error("unpatch", ex);
        }
        if (Harmony.HasAnyPatches(HarmonyId))
        {
            Log.Info("Performance ERROR: rollback incomplete, some patches remain; game is NOT vanilla. Remove or update the mod.");
            throw new InvalidOperationException("patches of " + HarmonyId + " still present after UnpatchAll");
        }
    }

    /// <summary>Test seam for the per-family unpatch of <see cref="ApplyRetargetFallback"/>.</summary>
    private static Action<MethodBase>? _unpatchFamilyOverride;

    internal static void OverrideUnpatchFamily(Action<MethodBase>? unpatch) => _unpatchFamilyOverride = unpatch;

    /// <summary>
    /// Called once per frame before the update (<see cref="BlueprintEvaluationScope"/>), never from a JIT callback: removes this
    /// mod's patch from every shared generic target whose recompiled body could not be retargeted (alias or failure), so it runs
    /// vanilla consistently. These are performance-only patches, so removing them keeps sim results identical. If a removal
    /// fails, the remaining failed families are still removed (best effort) and the frame fails through the game's own fatal
    /// path (exception out of Director.DoUpdate → GenericApp.OnUnhandledException: crash log, error dialog, exit). No full
    /// rollback: that would drop the multiplayer desync fixes mid-session and the peers would run different rules.
    /// </summary>
    internal static void ApplyRetargetFallback()
    {
        if (!SharedGenericRetarget.Failed) return;
        Action<MethodBase> unpatch = _unpatchFamilyOverride ?? (target => new Harmony(HarmonyId).Unpatch(target, HarmonyPatchType.All, HarmonyId));
        List<Exception>? errors = null;
        foreach (MethodBase target in SharedGenericRetarget.TakeFailed())
        {
            try
            {
                unpatch(target);
                Log.Info($"Shared generic patch {target.DeclaringType?.Name}.{target.Name} could not follow recompilation (another detour on the same code); unpatched, runs vanilla.");
            }
            catch (Exception ex)
            {
                Log.Error("retarget-fallback", ex);
                (errors ??= []).Add(ex);
            }
        }
        if (errors == null) return;
        if (!SharedGenericRetarget.Disable()) Log.Info("Performance: shared generic retarget listener could not be removed (inert).");
        Log.Info("Performance FATAL: a shared generic patch could not be removed after recompilation; stopping the game.");
        throw new InvalidOperationException("Performance mod: failed to remove a shared generic patch that no longer intercepts its recompiled code", new AggregateException(errors));
    }
    /// <summary>Unchanged IL: patching alone recompiles the method without its stale inlined copy.</summary>
    public static IEnumerable<CodeInstruction> Rejit(IEnumerable<CodeInstruction> instructions) => instructions;

    /// <summary>
    /// Whole body must be exactly the vanilla IL the patch was written against (opcodes, operands, labels, exception blocks),
    /// else throws so Patcher rolls back to vanilla. Compared by SHA-256 of Harmony's instruction listing, with labels
    /// renumbered by first use (Harmony's label ids depend on what else is patched into the same method).
    /// </summary>
    internal static List<CodeInstruction> RequireIl(IEnumerable<CodeInstruction> instructions, string sha256, string method)
    {
        List<CodeInstruction> code = instructions.ToList();
        var ids = new Dictionary<Label, int>();
        string Id(Label l) => "L" + (ids.TryGetValue(l, out int n) ? n : ids[l] = ids.Count);
        // Operands such as ldc.r4 3.4E+38 format by the current culture (the game does not fix it); hash must not.
        CultureInfo culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        string listing;
        try
        {
            listing = Listing();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
        string Listing() => string.Join("\n", code.Select(ci =>
            string.Join(",", ci.labels.Select(Id)) + ": "
            + new CodeInstruction(ci.opcode, ci.operand switch
            {
                Label l => Id(l),
                Label[] ls => string.Join(",", ls.Select(Id)),
                _ => ci.operand,
            })
            + " " + string.Join(",", ci.blocks.Select(b => b.blockType))));
        string actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(listing)));
        if (actual != sha256)
        {
            throw new InvalidOperationException($"{method} IL changed (sha256 {actual}):\n  " + listing.Replace("\n", "\n  "));
        }
        return code;
    }

    /// <summary>
    /// Stops the JIT from inlining <paramref name="method"/> into code compiled later (e.g. a patched caller rebuilt from
    /// IL), so such callers reach its detour. Already compiled inlined copies are not affected.
    /// </summary>
    internal static bool TryDisableInlining(MethodBase method, out string? error) =>
        TryDisableInlining(typeof(Harmony).Assembly.GetType("MonoMod.Core.Platforms.PlatformTriple"), method, out error);

    internal static bool TryDisableInlining(Type? platformType, MethodBase method, out string? error)
    {
        try
        {
            object? platform = platformType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            MethodInfo? disable = platformType?.GetMethod("TryDisableInlining", BindingFlags.Public | BindingFlags.Instance, null, [typeof(MethodBase)], null);
            if (platform == null || disable?.ReturnType != typeof(bool) || disable.Invoke(platform, [method]) is not true)
            {
                error = "Installed MonoMod cannot disable future inlining of " + method;
                return false;
            }
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = "MonoMod DisableInlining failed for " + method + ": " + (ex.InnerException ?? ex).Message;
            return false;
        }
    }

    /// <summary>Precompiled methods in the target module containing an inlined copy of <paramref name="patched"/>. Throws if unsafe.</summary>
    internal static List<MethodBase> FindR2RInliners(MethodBase patched)
    {
        Module module = patched.Module;
        var result = new List<MethodBase>();
        Dictionary<int, List<int>>? table = R2RInlineInfo.Read(module.Assembly.Location);
        if (table == null || !table.TryGetValue(R2RInlineInfo.Rid(patched), out List<int>? inlinerRids))
        {
            return result; // pure IL image, or nothing inlined it ahead of time
        }
        foreach (int rid in new HashSet<int>(inlinerRids))
        {
            MethodBase inliner = module.ResolveMethod(0x06000000 | rid)
                ?? throw new InvalidOperationException($"cannot resolve inliner RID {rid}");
            if (inliner.ContainsGenericParameters)
            {
                throw new NotSupportedException($"{inliner.DeclaringType?.FullName}.{inliner.Name} is shared generic code; refusing to patch it");
            }
            result.Add(inliner);
        }
        return result;
    }
}
