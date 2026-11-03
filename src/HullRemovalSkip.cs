using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Repairs;
using Halfling.Geometry;
using Halfling.Pooling;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>
/// Every part removed from a ship (in combat: every destroyed part) throws its convex hull away
/// (CommonBasePartsManager.RemovePart disposes <c>_convexHull</c>), and the queued bounding circle update rebuilds it from
/// scratch: a corner scan over all parts, a sort and a monotone chain, about 1 ms per combat tick in a 40-ship battle.
/// <para/>
/// Most destroyed parts are inside the ship, and then the hull cannot change. A hull point is a corner of a part's
/// bounding rect whose three outer neighbour cells are empty. Removing part P only empties cells of P's bounding rect, so
/// points it adds lie on corners of those cells and points it drops are P's own corners, all within P's closed bounding
/// rect (Left..Right, Top..Bottom). If no hull vertex lies in that closed rect and the rect lies inside the hull, the point
/// set keeps the same strict convex hull, and vanilla's monotone chain (sorted points, collinear points dropped) returns
/// the same vertices in the same order.
/// <para/>
/// So after vanilla RemovePart has finished, unchanged, a removal that passes that test refills the cache with a fresh
/// pooled list holding the old hull's points, which is what the next RefreshConvexHull would compute. Everything else
/// stays vanilla: MarkBoundingCircleDirty, the queued update, the circle computation from the hull, world-cache resets and
/// BoundingCircleChanged. The hull is only valid while the cache holds one, so any addition (vanilla discards the hull),
/// an ineligible removal, or a part change made while the cache is empty leaves the rebuild to vanilla. A removal that
/// mutates the parts again before it returns (nested removal, addition, exception) or removes the last part is not refilled.
/// A removal is refilled only while every part lies within ±<see cref="SafeCoordinate"/> cells, where none of vanilla's
/// int cross products can overflow. Only exact <see cref="PartsManager"/> and <see cref="DestroyedPartsManager"/> instances, through their removal entry
/// points (the only callers of the shared generic RemovePart); blueprints stay vanilla. Shared generic code is not
/// patched. Off: <c>UNOFFICIAL_PATCH_HULL_REMOVAL_SKIP=0</c>.
/// </summary>
internal static class HullRemovalSkip
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("UNOFFICIAL_PATCH_HULL_REMOVAL_SKIP"));
    internal static bool IsRequested(string? value) => value != "0";

    private const string PartsIlSha256 = "ECBC8527BB6BC4E5F54EB2F71076C8E60A7DD135D9DED8C4AED7B5E41137E0C6";
    private const string DestroyedIlSha256 = "BACB3E395EEEF3F2B785C70BB024C730FAB5214BCE0F98213FA1B7007AD32890";
    private const string NativeIlSha256 = "468D386C27F453819B46CFBF55321C59A6D1D9D2596E93C3CB797060809240CF";

    internal static readonly MethodInfo PartsTarget = typeof(PartsManager).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        .SingleOrDefault(m => m.Name == nameof(PartsManager.RemovePart) && m.GetParameters().Length == 5)
        ?? throw new MissingMethodException(nameof(PartsManager), "RemovePart(Part, CrewAction, bool, bool, in PartReplacementInfo?)");

    internal static readonly MethodInfo DestroyedTarget = AccessTools.Method(typeof(DestroyedPartsManager), nameof(DestroyedPartsManager.RemoveDestroyedPart), [typeof(DestroyedPart)])
        ?? throw new MissingMethodException(nameof(DestroyedPartsManager), nameof(DestroyedPartsManager.RemoveDestroyedPart));

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? [(PartsTarget, typeof(PartsHook)), (DestroyedTarget, typeof(DestroyedHook))] : [];

    private static readonly AccessTools.FieldRef<List<Part>, int> PartsVersion = AccessTools.FieldRefAccess<List<Part>, int>("_version");
    private static readonly AccessTools.FieldRef<List<DestroyedPart>, int> DestroyedVersion = AccessTools.FieldRefAccess<List<DestroyedPart>, int>("_version");

    /// <summary>
    /// The vanilla code the proof relies on: the only part mutations (AddPart/RemovePart discard the hull, nothing else
    /// writes it), the hull build and the corner scan with its neighbour test and cell lookup.
    /// </summary>
    internal static IEnumerable<MethodBase> NativeDependencies()
    {
        Type t = typeof(CommonBasePartsManager<Part>);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        string[] names =
        [
            "AddPart", "RemovePart", "RefreshConvexHull", "<RefreshConvexHull>g___CalculateConvexHull|188_0",
            "GetBoundingPoints", "<GetBoundingPoints>g___IsEmpty|189_0", "IsDoingTempLightweightUpdate",
        ];
        foreach (string name in names)
            yield return t.GetMethod(name, flags) ?? throw new MissingMethodException(t.Name, name);
        foreach (MethodInfo getter in t.GetProperties(flags).Where(p => p.Name == "Item").Select(p => p.GetMethod!).OrderBy(m => m.GetParameters().Length))
            yield return getter;
    }

    private static List<CodeInstruction> Pin(IEnumerable<CodeInstruction> instructions, string sha256, string name)
    {
        _ = Patcher.RequireIl(NativeDependencies().SelectMany(m => PatchProcessor.GetOriginalInstructions(m)), NativeIlSha256, "Hull removal skip native dependencies");
        return Patcher.RequireIl(instructions, sha256, name);
    }

    /// <summary>Per removal, in Harmony's __state: whether the cache is refilled afterwards, and the parts list version expected then.</summary>
    internal struct Pending
    {
        /// <summary>This scope's Prefix ran (Harmony still runs the Finalizer when an earlier prefix skipped or threw before it).</summary>
        internal bool Entered;
        internal bool Armed;
        internal int Version;
    }

    // One armed removal per thread: a nested removal runs vanilla and cancels the outer refill.
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static bool _nested;
    [ThreadStatic] private static IntVector2[]? _hull;
    [ThreadStatic] private static int _hullCount;

    internal static class PartsHook
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Pin(instructions, PartsIlSha256, "PartsManager.RemovePart");

        public static void Prefix(PartsManager __instance, Part part, out Pending __state) =>
            __state = Enter(__instance, part, __instance.GetType() == typeof(PartsManager), PartsVersion);

        public static void Finalizer(PartsManager __instance, Pending __state, Exception? __exception) =>
            Exit(__instance, __state, __exception, PartsVersion);
    }

    internal static class DestroyedHook
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Pin(instructions, DestroyedIlSha256, "DestroyedPartsManager.RemoveDestroyedPart");

        public static void Prefix(DestroyedPartsManager __instance, DestroyedPart part, out Pending __state) =>
            __state = Enter(__instance, part, __instance.GetType() == typeof(DestroyedPartsManager), DestroyedVersion);

        public static void Finalizer(DestroyedPartsManager __instance, Pending __state, Exception? __exception) =>
            Exit(__instance, __state, __exception, DestroyedVersion);
    }

    internal static Pending Enter<TPart>(CommonBasePartsManager<TPart> manager, TPart? part, bool exactType, AccessTools.FieldRef<List<TPart>, int> version)
        where TPart : CommonBasePart
    {
        if (_depth++ > 0)
        {
            _nested = true;
            return new Pending { Entered = true };
        }
        _nested = false;
        try
        {
            TempList<IntVector2>? hull = manager._convexHull;
            if (!exactType || part == null || hull == null || manager.Count < 2 || !ReferenceEquals(part.Ship, manager.Ship) ||
                manager.IsDoingTempLightweightUpdate() || part.Rules.BoundingRectType is not (PartRectType.Normal or PartRectType.Physical) ||
                !(KnownSafe(manager._orderedParts, version) || InSafeDomain(manager)) || !Preserves(hull, part.GetRect(part.Rules.BoundingRectType)))
                return new Pending { Entered = true };
            IntVector2[] copy = _hull is { } h && h.Length >= hull.Count ? h : _hull = new IntVector2[Math.Max(64, hull.Count)];
            for (int i = 0; i < hull.Count; i++) copy[i] = hull[i];
            _hullCount = hull.Count;
            // Vanilla RemovePart removes the part from _orderedParts exactly once (one version step).
            return new Pending { Entered = true, Armed = true, Version = unchecked(version(manager._orderedParts) + 1) };
        }
        catch (Exception ex)
        {
            Log.Error("hull-removal-skip", ex);
            return new Pending { Entered = true };
        }
    }

    internal static void Exit<TPart>(CommonBasePartsManager<TPart> manager, Pending state, Exception? exception, AccessTools.FieldRef<List<TPart>, int> version)
        where TPart : CommonBasePart
    {
        if (!state.Entered || --_depth > 0 || !state.Armed || _nested || exception != null) return;
        try
        {
            if (manager.Count == 0 || version(manager._orderedParts) != state.Version) return;
            // Proven safe before this one-step removal; removing a part cannot widen the bounds.
            SafeAt.GetValue(manager._orderedParts, static _ => new StrongBox<int>()).Value = state.Version;
            // Same double-checked lock as RefreshConvexHull; a hull rebuilt meanwhile is the same and stays.
            if (manager._convexHull != null) return;
            lock (manager)
            {
                if (manager._convexHull != null) return;
                TempList<IntVector2> hull = TempList<IntVector2>.Alloc();
                IntVector2[] points = _hull!;
                for (int i = 0; i < _hullCount; i++) hull.Add(points[i]);
                manager._convexHull = hull;
            }
        }
        catch (Exception ex)
        {
            Log.Error("hull-removal-skip", ex);
        }
    }

    /// <summary>
    /// True when removing a part with bounding rect <paramref name="rect"/> keeps <paramref name="hull"/> (vanilla's
    /// counter-clockwise strict hull, at least three vertices): no vertex in the closed rect and all four rect corners
    /// inside or on the hull.
    /// </summary>
    internal static bool Preserves(IReadOnlyList<IntVector2> hull, IntRect rect)
    {
        int n = hull.Count;
        if (n < 3 || rect.Width <= 0 || rect.Height <= 0) return false;
        int left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom;
        if (!Safe(left, top) || !Safe(right, bottom)) return false;
        for (int i = 0; i < n; i++)
        {
            IntVector2 v = hull[i];
            if (!Safe(v.X, v.Y)) return false;
            if (left <= v.X && v.X <= right && top <= v.Y && v.Y <= bottom) return false;
        }
        return Inside(hull, left, top) && Inside(hull, right, top) && Inside(hull, left, bottom) && Inside(hull, right, bottom);
    }

    /// <summary>
    /// Vanilla's hull build compares int cross products (a - o).Cross(b - o) = dx1 * dy2 - dx2 * dy1, which can overflow;
    /// the geometric argument holds only where none can. With every point coordinate in [-L, L] each difference is at most
    /// 2L, each product at most 4L², the cross product at most 8L², and 8 * 16383² = 2,147,221,512 &lt;= int.MaxValue
    /// (16384 would overflow).
    /// </summary>
    internal const int SafeCoordinate = 16383;

    private static bool Safe(int x, int y) => x >= -SafeCoordinate && x <= SafeCoordinate && y >= -SafeCoordinate && y <= SafeCoordinate;

    /// <summary>
    /// Every hull build input is a corner of a part's bounding rect, which lies inside its normal rect (PhysicalRect is
    /// checked to be inside the part's base rect when rules load), so the normal-rect bounds of all parts bound every point
    /// before and after the removal. Uses vanilla's cached bounding rect when present, else the same union (not cached).
    /// </summary>
    internal static bool InSafeDomain<TPart>(CommonBasePartsManager<TPart> manager) where TPart : CommonBasePart
    {
        if (manager._cachedBoundingRect is { } cached) return Safe(cached.Left, cached.Top) && Safe(cached.Right, cached.Bottom);
        List<TPart> parts = manager._orderedParts;
        for (int i = 0; i < parts.Count; i++)
        {
            IntRect r = parts[i].Rect;
            if (!Safe(r.Left, r.Top) || !Safe(r.Right, r.Bottom)) return false;
        }
        return true;
    }

    /// <summary>
    /// Parts list version at which <see cref="InSafeDomain"/> is known to hold: set after a refilled one-step removal,
    /// so the next removal from the same list skips the O(parts) rescan (vanilla clears the cached bounding rect on every
    /// removal). Any other mutation changes the version.
    /// </summary>
    private static readonly ConditionalWeakTable<object, StrongBox<int>> SafeAt = new();

    internal static bool KnownSafe<TPart>(List<TPart> parts, AccessTools.FieldRef<List<TPart>, int> version) =>
        SafeAt.TryGetValue(parts, out StrongBox<int>? box) && box.Value == version(parts);

    /// <summary>Test seam: this thread's open removal scopes.</summary>
    internal static int Depth => _depth;

    private static bool Inside(IReadOnlyList<IntVector2> hull, int x, int y)
    {
        int n = hull.Count;
        IntVector2 a = hull[n - 1];
        for (int i = 0; i < n; i++)
        {
            IntVector2 b = hull[i];
            if (((long)b.X - a.X) * ((long)y - a.Y) - ((long)b.Y - a.Y) * ((long)x - a.X) < 0) return false;
            a = b;
        }
        return true;
    }
}
