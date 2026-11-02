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
/// bounding circle are bit-identical. The lookups read a dense per-call copy of _partsByCell over its bounding box (built in one pass)
/// instead of hashing every neighbour cell. Patched once for CommonBasePartsManager&lt;Part&gt;; BlueprintPart and DestroyedPart share the same
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
        var grid = new Grid<TPart>(cells);
        try
        {
            int count = parts.Count;
            for (int i = 0; i < count; i++)
            {
                TPart part = parts[i];
                IntRect rect = part.GetRect(part.Rules.BoundingRectType);
                IntVector2 topLeft = rect.TopLeft;
                if (grid.IsEmpty(topLeft.X - 1, topLeft.Y - 1) && grid.IsEmpty(topLeft.X - 1, topLeft.Y) && grid.IsEmpty(topLeft.X, topLeft.Y - 1))
                    points.Add(topLeft);
                IntVector2 topRight = rect.TopRight;
                if (grid.IsEmpty(topRight.X, topRight.Y - 1) && grid.IsEmpty(topRight.X, topRight.Y) && grid.IsEmpty(topRight.X - 1, topRight.Y - 1))
                    points.Add(topRight);
                IntVector2 bottomLeft = rect.BottomLeft;
                if (grid.IsEmpty(bottomLeft.X - 1, bottomLeft.Y) && grid.IsEmpty(bottomLeft.X - 1, bottomLeft.Y - 1) && grid.IsEmpty(bottomLeft.X, bottomLeft.Y))
                    points.Add(bottomLeft);
                IntVector2 bottomRight = rect.BottomRight;
                if (grid.IsEmpty(bottomRight.X, bottomRight.Y) && grid.IsEmpty(bottomRight.X, bottomRight.Y - 1) && grid.IsEmpty(bottomRight.X - 1, bottomRight.Y))
                    points.Add(bottomRight);
            }
        }
        finally
        {
            grid.Release();
        }
    }

    [ThreadStatic] private static object?[]? t_grid;

    /// <summary>
    /// _partsByCell copied into a dense array over its bounding box: one pass over the entries instead of up to twelve hash
    /// lookups per part. A sparse map (box much larger than the cell count) keeps the dictionary. Per thread: the hull can
    /// be built lazily by readers in parallel buckets.
    /// </summary>
    private readonly ref struct Grid<TPart> where TPart : CommonBasePart
    {
        private readonly Dictionary<IntVector2, TPart> _map;
        private readonly object?[]? _cells;
        private readonly int _minX, _minY, _width, _height;

        internal Grid(Dictionary<IntVector2, TPart> map)
        {
            _map = map;
            _cells = null;
            _minX = _minY = _width = _height = 0;
            if (map.Count == 0) return;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (IntVector2 cell in map.Keys)
            {
                if (cell.X < minX) minX = cell.X;
                if (cell.X > maxX) maxX = cell.X;
                if (cell.Y < minY) minY = cell.Y;
                if (cell.Y > maxY) maxY = cell.Y;
            }
            long width = (long)maxX - minX + 1, height = (long)maxY - minY + 1;
            if (width > 1 << 16 || height > 1 << 16) return; // huge coordinates: keep the dictionary (and no overflow below)
            long area = width * height;
            if (area > 4L * map.Count + 4096) return; // shortcut: sparse map stays on the dictionary
            object?[] cells = t_grid ??= new object?[4096];
            if (cells.Length < area) t_grid = cells = new object?[Math.Max(area, cells.Length * 2)];
            foreach (KeyValuePair<IntVector2, TPart> kv in map)
                cells[(kv.Key.Y - minY) * width + (kv.Key.X - minX)] = kv.Value;
            _cells = cells;
            _minX = minX;
            _minY = minY;
            _width = (int)width;
            _height = (int)height;
        }

        /// <summary>
        /// Vanilla <c>_IsEmpty</c>: no part at the cell, or the cell lies outside that part's bounding rect. A part found by
        /// cell lies in its normal rect by construction; other rect types (Physical, or an unknown value that throws) are
        /// tested as vanilla does.
        /// </summary>
        internal bool IsEmpty(int x, int y)
        {
            CommonBasePart? part;
            if (_cells == null)
            {
                if (!_map.TryGetValue(new IntVector2(x, y), out TPart? found)) return true;
                part = found;
            }
            else
            {
                int gx = x - _minX, gy = y - _minY;
                if ((uint)gx >= (uint)_width || (uint)gy >= (uint)_height) return true;
                part = (CommonBasePart?)_cells[gy * _width + gx]; // never cast to TPart: shared code may run for BlueprintPart
                if (part == null) return true;
            }
            PartRectType type = part.Rules.BoundingRectType;
            return type != PartRectType.Normal && !part.GetRect(type).Contains(new IntVector2(x, y));
        }

        /// <summary>Drops the part references (the array is reused by this thread).</summary>
        internal void Release()
        {
            if (_cells != null) Array.Clear(_cells, 0, _width * _height);
        }
    }
}