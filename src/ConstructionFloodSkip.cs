using System;
using System.Reflection;
using Cosmoteer.Ships.Construction;
using Halfling.Geometry;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>
/// After a part is destroyed or removed, ConstructionManager.RefreshUnderlyingPartRemoves floods outward from its
/// neighbours through the ship (priority queue, hash sets, an iterator per visited part) until all neighbours are
/// connected again, and on the way refreshes queued underlying-part removals and queued per-tile constructions. A ship
/// with no queued removal and no queued construction has nothing to refresh, so the flood only computes and discards
/// (about 1 ms per combat tick with 40 ships). It is now skipped when both queues are empty. Off:
/// <c>UNOFFICIAL_PATCH_CONSTRUCTION_FLOOD_SKIP=0</c>.
/// </summary>
internal static class ConstructionFloodSkip
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("UNOFFICIAL_PATCH_CONSTRUCTION_FLOOD_SKIP"));
    internal static bool IsRequested(string? value) => value != "0";

    internal static readonly MethodInfo Target = AccessTools.Method(typeof(ConstructionManager), "RefreshUnderlyingPartRemoves", [typeof(IntRect)])
        ?? throw new MissingMethodException(nameof(ConstructionManager), "RefreshUnderlyingPartRemoves");

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? [(Target, typeof(ConstructionFloodSkip))] : [];

    private const string IlSha256 = "8BF9108C19A497B74E0EFFA5982047DD8AFC1460A368576DE30FB1A699D63B97";

    /// <summary>Pins the body the skip condition was derived from (its only side effects go through the two queues).</summary>
    public static System.Collections.Generic.IEnumerable<CodeInstruction> Transpiler(System.Collections.Generic.IEnumerable<CodeInstruction> instructions) =>
        Patcher.RequireIl(instructions, IlSha256, "ConstructionManager.RefreshUnderlyingPartRemoves");

    /// <summary>Runs vanilla only when a queued removal or construction could be refreshed.</summary>
    public static bool Prefix(ConstructionManager __instance) => __instance._partRemoves.Count != 0 || __instance._partAddsByCell.Count != 0;
}
