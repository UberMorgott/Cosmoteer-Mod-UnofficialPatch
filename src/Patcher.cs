using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Cosmoteer.Source.Ships.Blueprints;
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
/// The patches change no sim result (same calls, same values), so a mixed lobby with and without the mod stays in sync.
/// </summary>
internal static class Patcher
{
    public const string HarmonyId = "morgott.performance";

    internal static MethodInfo Target =>
        AccessTools.Method(typeof(BaseBlueprintPartNetworkPort), nameof(BaseBlueprintPartNetworkPort.UpdateOperational))
        ?? throw new MissingMethodException(nameof(BaseBlueprintPartNetworkPort), nameof(BaseBlueprintPartNetworkPort.UpdateOperational));

    /// <summary>True only when every patch is installed.</summary>
    public static bool Apply()
    {
        var sw = Stopwatch.StartNew();
        var harmony = new Harmony(HarmonyId);
        try
        {
            MethodInfo target = Target;
            List<MethodBase> inliners = FindR2RInliners(target); // pure read, before anything is patched
            harmony.Patch(target, transpiler: new HarmonyMethod(AccessTools.Method(typeof(PortToggleMode), nameof(PortToggleMode.Transpiler))));
            var rejit = new HarmonyMethod(AccessTools.Method(typeof(Patcher), nameof(Rejit)));
            foreach (MethodBase inliner in inliners)
            {
                harmony.Patch(inliner, transpiler: rejit);
                Log.Info($"  R2R inliner patched: {inliner.DeclaringType?.FullName}.{inliner.Name}");
            }
            Log.Info($"Patched 1 method + {inliners.Count} R2R inliners in {sw.ElapsedMilliseconds} ms");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("patch", ex);
            try
            {
                harmony.UnpatchAll(HarmonyId);
            }
            catch (Exception unpatchEx)
            {
                // Not fatal: every patch keeps vanilla sim behavior, so a partial state cannot desync.
                Log.Error("unpatch", unpatchEx);
            }
            Log.Info("Performance DISABLED: patches rolled back, game runs vanilla.");
            return false;
        }
    }

    /// <summary>Unchanged IL: patching alone recompiles the method without its stale inlined copy.</summary>
    public static IEnumerable<CodeInstruction> Rejit(IEnumerable<CodeInstruction> instructions) => instructions;

    /// <summary>Precompiled methods of Cosmoteer.dll containing an inlined copy of <paramref name="patched"/>. Throws if unsafe.</summary>
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
