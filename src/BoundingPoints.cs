using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Ships.Parts;
using Halfling.Geometry;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Every time a ship gains or loses a part (in combat: every destroyed part) its convex hull is rebuilt on the next read
/// (bounding circle, nebula, separation, AI), and vanilla CommonBasePartsManager.GetBoundingPoints checks the three
/// outside neighbours of each corner of every part: up to twelve indexer lookups per part, each followed by recomputing
/// the neighbour part's rect to test whether the cell lies in it (about 9% of the serial main thread in slow combat ticks).
/// <para/>
/// The replacement looks the neighbour up in _partsByCell directly. A part is stored only under the cells of its normal
/// rect (AddPart/RemovePart via GetCells(), Location/Rotation/Rules fixed after construction), so for a neighbour whose
/// BoundingRectType is Normal the hit already proves the cell lies in that rect and the rect is not rebuilt; Physical
/// neighbours keep vanilla's physical-rect test. Same parts, same corners, same order of points, so the hull and the
/// bounding circle are bit-identical. Patched once for CommonBasePartsManager&lt;Part&gt;; BlueprintPart and DestroyedPart share the same
/// reference-type code. Off: <c>PERFORMANCE_BOUNDING_POINTS=0</c>.
/// </summary>
internal static class BoundingPoints
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_BOUNDING_POINTS"));
    internal static bool IsRequested(string? value) => value != "0";

    private const string IlSha256 = "4B6DB94D272042AE90FF6229961999314ACC99809D2027CDED96678C47069250";

    internal static readonly MethodInfo Target = AccessTools.Method(typeof(CommonBasePartsManager<Part>), "GetBoundingPoints", [typeof(List<IntVector2>)])
        ?? throw new MissingMethodException(nameof(CommonBasePartsManager<Part>), "GetBoundingPoints");

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? [(Target, typeof(BoundingPoints))] : [];

    /// <summary>Replaces the whole body with <c>Fill&lt;TPart&gt;(this, points)</c>.</summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        _ = Patcher.RequireIl(instructions, IlSha256, "CommonBasePartsManager.GetBoundingPoints");
        Type part = original.DeclaringType!.GetGenericArguments()[0];
        return
        [
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BoundingPoints), nameof(Fill)).MakeGenericMethod(part)),
            new CodeInstruction(OpCodes.Ret),
        ];
    }

    /// <summary>Vanilla GetBoundingPoints: same corners, same neighbour tests, same evaluation and output order.</summary>
    internal static void Fill<TPart>(CommonBasePartsManager<TPart> manager, List<IntVector2> points) where TPart : CommonBasePart
    {
        List<TPart> parts = manager._orderedParts;
        Dictionary<IntVector2, TPart> cells = manager._partsByCell;
        int count = parts.Count;
        for (int i = 0; i < count; i++)
        {
            TPart part = parts[i];
            IntRect rect = part.GetRect(part.Rules.BoundingRectType);
            IntVector2 topLeft = rect.TopLeft;
            if (IsEmpty(cells, topLeft.X - 1, topLeft.Y - 1) && IsEmpty(cells, topLeft.X - 1, topLeft.Y) && IsEmpty(cells, topLeft.X, topLeft.Y - 1))
                points.Add(topLeft);
            IntVector2 topRight = rect.TopRight;
            if (IsEmpty(cells, topRight.X, topRight.Y - 1) && IsEmpty(cells, topRight.X, topRight.Y) && IsEmpty(cells, topRight.X - 1, topRight.Y - 1))
                points.Add(topRight);
            IntVector2 bottomLeft = rect.BottomLeft;
            if (IsEmpty(cells, bottomLeft.X - 1, bottomLeft.Y) && IsEmpty(cells, bottomLeft.X - 1, bottomLeft.Y - 1) && IsEmpty(cells, bottomLeft.X, bottomLeft.Y))
                points.Add(bottomLeft);
            IntVector2 bottomRight = rect.BottomRight;
            if (IsEmpty(cells, bottomRight.X, bottomRight.Y) && IsEmpty(cells, bottomRight.X, bottomRight.Y - 1) && IsEmpty(cells, bottomRight.X - 1, bottomRight.Y))
                points.Add(bottomRight);
        }
    }

    /// <summary>
    /// Vanilla <c>_IsEmpty</c>: no part at the cell, or the cell lies outside that part's bounding rect. A part found by
    /// cell lies in its normal rect by construction; other rect types (Physical, or an unknown value that throws) are
    /// tested as vanilla does.
    /// </summary>
    private static bool IsEmpty<TPart>(Dictionary<IntVector2, TPart> cells, int x, int y) where TPart : CommonBasePart
    {
        var cell = new IntVector2(x, y);
        if (!cells.TryGetValue(cell, out TPart? part))
            return true;
        PartRectType type = part.Rules.BoundingRectType;
        return type != PartRectType.Normal && !part.GetRect(type).Contains(cell);
    }
}
