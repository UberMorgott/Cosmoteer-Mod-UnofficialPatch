using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>Each target and the class holding its public static Prefix / Postfix / Transpiler (any subset).</summary>
    internal static (MethodBase Target, Type Patch)[] Patches =>
    [
        (Target, typeof(PortToggleMode)),
        (ThrusterAccelerationCache.Target, typeof(ThrusterAccelerationCache)),
        (MapNodeText.Target, typeof(MapNodeText)),
    ];

    /// <summary>True only when every patch is installed.</summary>
    public static bool Apply()
    {
        var sw = Stopwatch.StartNew();
        var harmony = new Harmony(HarmonyId);
        try
        {
            var patches = Patches.Select(p => (p.Target, p.Patch, Inliners: FindR2RInliners(p.Target))).ToList(); // pure read, before anything is patched
            var rejit = new HarmonyMethod(AccessTools.Method(typeof(Patcher), nameof(Rejit)));
            int inlinerCount = 0;
            foreach ((MethodBase target, Type patch, List<MethodBase> inliners) in patches)
            {
                harmony.Patch(target, Hook(patch, "Prefix"), Hook(patch, "Postfix"), Hook(patch, "Transpiler"));
                foreach (MethodBase inliner in inliners)
                {
                    harmony.Patch(inliner, transpiler: rejit);
                    Log.Info($"  R2R inliner of {target.Name} patched: {inliner.DeclaringType?.FullName}.{inliner.Name}");
                }
                inlinerCount += inliners.Count;
            }
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
        string listing = string.Join("\n", code.Select(ci =>
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
