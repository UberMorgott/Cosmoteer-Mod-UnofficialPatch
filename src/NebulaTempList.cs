using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Nebulas;
using Cosmoteer.Ships;
using Halfling.Geometry;
using Halfling.Pooling;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>
/// Every sim tick, every awake ship refreshes its nebula state (SimShipsManager.UpdateShipsTree → RefreshShipPosition →
/// SimNebulasManager.RefreshShipNebulas), even on maps without any nebula. Vanilla GetInsideTypes(Ship) takes a pooled
/// TempList of hull points and never returns it, so the pool runs dry and each refresh allocates a fresh list that grows
/// to the hull size (garbage per moving ship per tick), then looks up six grid cells per hull point.
/// <para/>
/// The list is now returned to the pool (also when the lookup throws) and presized to the hull point count; the points,
/// their order and the lookup call are unchanged. RefreshShipNebulas returns at once when the map has no nebula cell and
/// the ship tracks no nebula: vanilla then finds no strength and sets each nebula type to 0, which is a no-op for a ship
/// without tracked nebulas (ShipNebulasTracker.SetNebulaStrength adds nothing at 0, _shipsByNebulaType is untouched).
/// The only other effect skipped is the lazy convex hull build, which other readers still do on demand from the same
/// parts. Zero-strength cleanup of tracked nebulas runs as before. Off: <c>UNOFFICIAL_PATCH_NEBULA_TEMPLIST=0</c>.
/// </summary>
internal static class NebulaTempList
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("UNOFFICIAL_PATCH_NEBULA_TEMPLIST"));
    internal static bool IsRequested(string? value) => value != "0";

    private const string IlSha256 = "3CC244994566922C9C4281533A9BD43A43183CCCC4F6CEBDCDE1A65ECF626B2E";

    internal static readonly MethodInfo Target = AccessTools.Method(typeof(SimNebulasManager), "GetInsideTypes", [typeof(Ship), typeof(ICollection<NebulaStrengthInfo>)])
        ?? throw new MissingMethodException(nameof(SimNebulasManager), "GetInsideTypes(Ship)");

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? [(Target, typeof(NebulaTempList)), (EarlyOut.RefreshTarget, typeof(EarlyOut))] : [];

    /// <summary>Replaces the whole body with <c>Fill(this, ship, strengths)</c>.</summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        _ = Patcher.RequireIl(instructions, IlSha256, "SimNebulasManager.GetInsideTypes(Ship)");
        return
        [
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Ldarg_2),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(NebulaTempList), nameof(Fill))),
            new CodeInstruction(OpCodes.Ret),
        ];
    }

    /// <summary>Vanilla GetInsideTypes(Ship) with the hull point list presized and returned to the pool.</summary>
    internal static void Fill(SimNebulasManager nebulas, Ship ship, ICollection<NebulaStrengthInfo> strengths)
    {
        IReadOnlyList<IntVector2> hull = ship.Parts.GetLocalConvexHullPoints();
        TempList<Vector2> points = TempList<Vector2>.Alloc();
        try
        {
            if (points.Capacity < hull.Count) points.Capacity = hull.Count;
            for (int i = 0; i < hull.Count; i++)
                points.Add(ship.DetTransformPointToWorld(hull[i]));
            nebulas.GetInsideTypes(points, strengths);
        }
        finally
        {
            points.Dispose();
        }
    }

    /// <summary>Prepends <c>if (EarlyOut.Skip(this, ship)) return;</c> to RefreshShipNebulas.</summary>
    internal static class EarlyOut
    {
        private const string RefreshIlSha256 = "9DCA43729035A0B60F1626FC2170545182B900AC770B4CA3DE179EE165C365D6";

        internal static readonly MethodInfo RefreshTarget = AccessTools.Method(typeof(SimNebulasManager), nameof(SimNebulasManager.RefreshShipNebulas))
            ?? throw new MissingMethodException(nameof(SimNebulasManager), nameof(SimNebulasManager.RefreshShipNebulas));

        // Harmony binds the hook by the name Transpiler.
        // ReSharper disable once MemberHidesStaticFromOuterClass
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            List<CodeInstruction> code = Patcher.RequireIl(instructions, RefreshIlSha256, "SimNebulasManager.RefreshShipNebulas");
            Label body = generator.DefineLabel();
            code[0].labels.Add(body);
            code.InsertRange(0,
            [
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EarlyOut), nameof(Skip))),
                new CodeInstruction(OpCodes.Brfalse, body),
                new CodeInstruction(OpCodes.Ret),
            ]);
            return code;
        }

        /// <summary>No nebula cell on the map and none tracked by the ship; a null ship keeps vanilla's throw.</summary>
        internal static bool Skip(SimNebulasManager nebulas, Ship? ship) =>
            ship != null && nebulas._grid.Count == 0 && ship.Nebulas.Count == 0;
    }
}
