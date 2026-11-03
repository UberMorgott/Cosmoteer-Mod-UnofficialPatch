using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Repairs;
using Halfling.Geometry;
using Halfling.Pooling;
using HarmonyLib;
using Xunit;

namespace UnofficialPatch.Tests;

[Collection(nameof(BoundingPointsTests))]
public sealed class HullRemovalSkipTests
{
    [Fact]
    public void KillSwitchIsOnlyZero()
    {
        Assert.False(HullRemovalSkip.IsRequested("0"));
        Assert.True(HullRemovalSkip.IsRequested(null));
        Assert.True(HullRemovalSkip.IsRequested("1"));
    }

    [Fact]
    public void TranspilersKeepRealGameIl()
    {
        (MethodInfo Target, Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>> Transpiler)[] hooks =
        [
            (HullRemovalSkip.PartsTarget, HullRemovalSkip.PartsHook.Transpiler),
            (HullRemovalSkip.DestroyedTarget, HullRemovalSkip.DestroyedHook.Transpiler),
        ];
        foreach ((MethodInfo target, Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>> transpiler) in hooks)
        {
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(target);
            List<CodeInstruction> code = transpiler(PatchProcessor.GetOriginalInstructions(target)).ToList();
            Assert.Equal(original.Select(i => (i.opcode, i.operand?.ToString())), code.Select(i => (i.opcode, i.operand?.ToString())));
        }
    }

    [Fact]
    public void TranspilerRejectsChangedIl()
    {
        List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(HullRemovalSkip.PartsTarget);
        code.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => HullRemovalSkip.PartsHook.Transpiler(code).ToList());
    }

    /// <summary>The removal entry points patched are the only callers of the shared generic RemovePart.</summary>
    [Fact]
    public void PatchedEntryPointsAreNotSharedGeneric()
    {
        Assert.False(SharedGenericRetarget.IsShared(HullRemovalSkip.PartsTarget));
        Assert.False(SharedGenericRetarget.IsShared(HullRemovalSkip.DestroyedTarget));
        Assert.Equal(2, HullRemovalSkip.Patches.Length);
    }

    /// <summary>
    /// Random ships (rotated, flipped, physical-rect parts, holes, two islands) destroyed part by part with refreshes at
    /// random points: whenever the skip keeps a hull it has the vanilla rebuild's ordered points and circle bits.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void RandomShipsMatchVanillaThroughDestruction(int seed)
    {
        var parts = Ship<PartsManager, Part>.Random(seed);
        var destroyed = Ship<DestroyedPartsManager, DestroyedPart>.Random(seed);
        var rand = new Random(seed * 31);
        (int kept, int compared) = Destroy(parts, rand, () => rand.Next(parts.Parts.Count));
        var rand2 = new Random(seed * 37);
        (int keptDestroyed, _) = Destroy(destroyed, rand2, () => rand2.Next(destroyed.Parts.Count));
        Assert.InRange(keptDestroyed, 10, int.MaxValue);
        Assert.InRange(compared, 25, int.MaxValue);
        Assert.InRange(kept, 10, int.MaxValue);
    }

    /// <summary>Long straight edges: removing parts in the middle of an edge (corners collinear with it) or inside keeps the hull.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 3)]
    public void SolidBlockEdgesAndInterior(int w, int h)
    {
        var ship = Ship<PartsManager, Part>.Block(40, 8, w, h);
        var rand = new Random(w * 10 + h);
        // Interior and edge-middle parts first (all eligible), then everything in random order.
        int edgeMiddle = ship.Parts.FindIndex(p => p.Location.X == 20 - 20 % w && p.Location.Y == 0);
        ship.Refresh();
        Assert.True(Remove(ship, edgeMiddle));
        (int kept, _) = Destroy(ship, rand, () => rand.Next(ship.Parts.Count));
        Assert.InRange(kept, 5, int.MaxValue);
    }

    /// <summary>Corner parts carry hull vertices: never kept, the vanilla rebuild runs.</summary>
    [Fact]
    public void CornerDestructionRebuilds()
    {
        var ship = Ship<PartsManager, Part>.Block(10, 6, 1, 1);
        foreach (IntVector2 corner in new[] { new IntVector2(0, 0), new IntVector2(9, 0), new IntVector2(0, 5), new IntVector2(9, 5) })
        {
            ship.Refresh();
            Assert.False(Remove(ship, ship.Parts.FindIndex(p => p.Location == corner)));
            AssertVanilla(ship, null);
        }
    }

    /// <summary>Checkerboard (diagonal contacts only) and the cells next to it.</summary>
    [Fact]
    public void DiagonalContacts()
    {
        var ship = Ship<PartsManager, Part>.Pattern(14, 14, (x, y) => (x + y) % 2 == 0 || (x > 3 && x < 10 && y > 3 && y < 10));
        var rand = new Random(17);
        (_, int compared) = Destroy(ship, rand, () => rand.Next(ship.Parts.Count));
        Assert.InRange(compared, 20, int.MaxValue);
    }

    /// <summary>Repeated splits: a ring with holes and bands cut through it, islands destroyed one after another.</summary>
    [Fact]
    public void RepeatedSplits()
    {
        var ship = Ship<PartsManager, Part>.Pattern(30, 30, (x, y) => !(x % 7 == 3 && y > 2) && !(y % 9 == 4 && x < 27) && (x - 15) * (x - 15) + (y - 15) * (y - 15) < 200);
        var rand = new Random(23);
        // Cut through the middle first, then random.
        (_, int compared) = Destroy(ship, rand, () =>
        {
            int band = ship.Parts.FindIndex(p => p.Location.Y == 15);
            return band >= 0 ? band : rand.Next(ship.Parts.Count);
        });
        Assert.InRange(compared, 20, int.MaxValue);
    }

    /// <summary>The last two parts: removing the last one never refills (vanilla then has no hull and no circle).</summary>
    [Fact]
    public void LastPartIsVanilla()
    {
        var ship = Ship<PartsManager, Part>.Block(3, 1, 1, 1);
        ship.Refresh();
        Assert.True(Remove(ship, 1)); // middle of a straight line: collinear, hull unchanged
        AssertVanilla(ship, ship.Hull());
        Assert.False(Remove(ship, 0));
        ship.Refresh();
        Assert.False(Remove(ship, 0));
        Assert.Empty(ship.Parts);
        Assert.Null(ship.Manager._convexHull);
    }

    [Fact]
    public void NoHullNestingVersionAndExceptionStayVanilla()
    {
        var ship = Ship<PartsManager, Part>.Block(8, 8, 1, 1);
        int inner = ship.Parts.FindIndex(p => p.Location == new IntVector2(3, 3));
        // No cached hull: nothing to keep.
        ship.Manager._convexHull?.Dispose();
        ship.Manager._convexHull = null;
        Assert.False(Remove(ship, inner));

        // Wrong runtime type.
        ship.Refresh();
        HullRemovalSkip.Pending wrongType = Enter(ship, ship.Parts[inner], exactType: false);
        Assert.False(wrongType.Armed);
        ship.NativeRemove(inner);
        Exit(ship, wrongType, null);
        Assert.Null(ship.Manager._convexHull);

        // Nested removal: inner runs vanilla, outer is not refilled.
        ship.Refresh();
        int a = ship.Parts.FindIndex(p => p.Location == new IntVector2(4, 4));
        HullRemovalSkip.Pending outer = Enter(ship, ship.Parts[a], exactType: true);
        Assert.True(outer.Armed);
        int b = ship.Parts.FindIndex(p => p.Location == new IntVector2(2, 4));
        HullRemovalSkip.Pending nested = Enter(ship, ship.Parts[b], exactType: true);
        Assert.False(nested.Armed);
        ship.NativeRemove(b);
        Exit(ship, nested, null);
        ship.NativeRemove(ship.Parts.FindIndex(p => p.Location == new IntVector2(4, 4)));
        Exit(ship, outer, null);
        Assert.Null(ship.Manager._convexHull);

        // A second mutation of the same parts list inside the removal (addition or other) changes the version.
        ship.Refresh();
        int c = ship.Parts.FindIndex(p => p.Location == new IntVector2(5, 5));
        HullRemovalSkip.Pending other = Enter(ship, ship.Parts[c], exactType: true);
        Assert.True(other.Armed);
        Part moved = ship.Parts[ship.Parts.FindIndex(p => p.Location == new IntVector2(5, 2))];
        ship.NativeRemove(c);
        ship.Parts.Remove(moved);
        ship.Parts.Add(moved);
        Exit(ship, other, null);
        Assert.Null(ship.Manager._convexHull);

        // Exception out of the removal.
        ship.Refresh();
        int d = ship.Parts.FindIndex(p => p.Location == new IntVector2(3, 5));
        HullRemovalSkip.Pending failed = Enter(ship, ship.Parts[d], exactType: true);
        Assert.True(failed.Armed);
        ship.NativeRemove(d);
        Exit(ship, failed, new InvalidOperationException());
        Assert.Null(ship.Manager._convexHull);

        // A hull rebuilt during the removal is kept (same points), not replaced.
        ship.Refresh();
        int e = ship.Parts.FindIndex(p => p.Location == new IntVector2(4, 5));
        HullRemovalSkip.Pending rebuilt = Enter(ship, ship.Parts[e], exactType: true);
        ship.NativeRemove(e);
        ship.Refresh();
        TempList<IntVector2> native = ship.Manager._convexHull!;
        Exit(ship, rebuilt, null);
        Assert.Same(native, ship.Manager._convexHull);
    }

    [Fact]
    public void PreservesNeedsClosedRectFreeOfVerticesAndInsideHull()
    {
        IntVector2[] square = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
        Assert.True(HullRemovalSkip.Preserves(square, new IntRect(4, 4, 2, 2)));
        Assert.True(HullRemovalSkip.Preserves(square, new IntRect(4, 0, 2, 1))); // on an edge, no vertex
        Assert.False(HullRemovalSkip.Preserves(square, new IntRect(9, 9, 1, 1))); // closed rect holds (10,10)
        Assert.False(HullRemovalSkip.Preserves(square, new IntRect(8, 4, 3, 1))); // sticks out
        Assert.False(HullRemovalSkip.Preserves(square, new IntRect(4, 4, 0, 2))); // degenerate
        Assert.False(HullRemovalSkip.Preserves(square[..2], new IntRect(4, 4, 1, 1)));
        IntVector2[] clockwise = [.. square.Reverse()];
        Assert.False(HullRemovalSkip.Preserves(clockwise, new IntRect(4, 4, 2, 2)));
    }

    /// <summary>
    /// Far from the origin vanilla's int cross products overflow, so geometry alone does not prove the rebuild unchanged
    /// (14 parts of 1x1 near ±70,000): no refill there, the vanilla rebuild runs. At the bound itself the skip still works.
    /// </summary>
    [Theory]
    [InlineData(70_000, -70_000, false)]
    [InlineData(-70_000, 70_000, false)]
    [InlineData(HullRemovalSkip.SafeCoordinate - 5, -HullRemovalSkip.SafeCoordinate, true)]
    [InlineData(HullRemovalSkip.SafeCoordinate - 4, 0, false)]
    public void OverflowDomainFallsBack(int ox, int oy, bool kept)
    {
        // 5x3 block minus one corner: 14 parts, Right = ox + 5.
        var ship = Ship<PartsManager, Part>.Pattern(5, 3, (x, y) => !(x == 4 && y == 2), ox, oy);
        Assert.Equal(14, ship.Parts.Count);
        ship.Refresh();
        Assert.Equal(kept, HullRemovalSkip.InSafeDomain(ship.Manager));
        int middle = ship.Parts.FindIndex(p => p.Location == new IntVector2(ox + 2, oy + 1));
        Assert.Equal(kept, Remove(ship, middle));
        IntVector2[]? mine = ship.Hull();
        ship.ClearHull();
        ship.Refresh();
        if (kept) Assert.Equal(mine, ship.Hull());
        else Assert.Null(mine);
    }

    private static int _mode; // 0 run, 1 earlier prefix skips, 2 earlier prefix throws
    private static int _nest;
    private static readonly List<int> SeenDepths = [];

    private static class Hooks
    {
        public static bool EarlyPrefix() => _mode switch { 1 => false, 2 => throw new InvalidOperationException("early"), _ => true };

        public static void LatePrefix(PartsManager __instance, Part part)
        {
            SeenDepths.Add(HullRemovalSkip.Depth);
            if (_nest-- > 0) __instance.ClearSpawnedFromDestroyed(part);
        }
    }

    /// <summary>
    /// Real Harmony patch: an earlier prefix that skips or throws keeps the hook's own prefix from running while its
    /// finalizer still runs; the scope count stays balanced and later nested removals still see their outer scope.
    /// </summary>
    [Fact]
    public void FinalizerWithoutPrefixKeepsScopesBalanced()
    {
        MethodInfo target = AccessTools.Method(typeof(PartsManager), nameof(PartsManager.ClearSpawnedFromDestroyed));
        var harmony = new Harmony("morgott.unofficialpatch.tests.hullremovalskip");
        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.EarlyPrefix)) { priority = Priority.First });
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(HullRemovalSkip.PartsHook), nameof(HullRemovalSkip.PartsHook.Prefix)),
                finalizer: new HarmonyMethod(typeof(HullRemovalSkip.PartsHook), nameof(HullRemovalSkip.PartsHook.Finalizer)));
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.LatePrefix)) { priority = Priority.Last });
            var ship = Ship<PartsManager, Part>.Block(6, 6, 1, 1);
            ship.Refresh();
            Part part = ship.Parts[ship.Parts.FindIndex(p => p.Location == new IntVector2(2, 2))];
            Assert.Equal(0, HullRemovalSkip.Depth);
            _mode = 1;
            ship.Manager.ClearSpawnedFromDestroyed(part);
            Assert.Equal(0, HullRemovalSkip.Depth);
            _mode = 2;
            for (int i = 0; i < 3; i++)
            {
                Assert.Throws<InvalidOperationException>(() => ship.Manager.ClearSpawnedFromDestroyed(part));
                Assert.Equal(0, HullRemovalSkip.Depth);
            }
            _mode = 0;
            SeenDepths.Clear();
            _nest = 2;
            ship.Manager.ClearSpawnedFromDestroyed(part);
            Assert.Equal([1, 2, 3], SeenDepths);
            Assert.Equal(0, HullRemovalSkip.Depth);
            Assert.NotNull(ship.Manager._convexHull); // nothing removed: vanilla cache untouched
        }
        finally
        {
            _mode = 0;
            _nest = 0;
            harmony.UnpatchAll("morgott.unofficialpatch.tests.hullremovalskip");
        }
    }

    /// <summary>
    /// Counterexample to geometry alone: 14 parts of 1x1 spread over ±70,000 cells. The removed part is strictly inside the
    /// hull and touches no vertex (exact long arithmetic accepts it), yet vanilla's overflowing int rebuild changes the
    /// vertex sequence. The domain guard refuses it; the cache stays empty and the result is vanilla's.
    /// </summary>
    [Fact]
    public void OverflowCounterexampleFallsBack()
    {
        IntVector2[] cells = [.. "-35187,-54496;-4619,38024;22052,-9411;-20429,62140;-55823,19943;-65991,-35276;-25185,68567;25497,21684;-30418,16148;28595,28251;62968,-56926;-47473,-16525;41712,-46275;41129,-26989"
            .Split(';').Select(s => s.Split(',')).Select(xy => new IntVector2(int.Parse(xy[0], CultureInfo.InvariantCulture), int.Parse(xy[1], CultureInfo.InvariantCulture)))];
        var removed = new IntVector2(41129, -26989);
        var ship = Ship<PartsManager, Part>.Cells(cells);
        ship.Refresh();
        IntVector2[] before = ship.Hull()!;
        int index = ship.Parts.FindIndex(p => p.Location == removed);
        Assert.True(PreservesRaw(before, ship.Parts[index].Rect));
        Assert.False(HullRemovalSkip.Preserves(before, ship.Parts[index].Rect));
        Assert.False(HullRemovalSkip.InSafeDomain(ship.Manager));
        Assert.False(Remove(ship, index));
        ship.Refresh();
        IntVector2[] native = ship.Hull()!;
        Assert.NotEqual(before, native); // vanilla's rebuild really differs
        var reference = Ship<PartsManager, Part>.Cells(cells.Where(c => c != removed));
        reference.Refresh();
        Assert.Equal(reference.Hull(), native);
    }
    private static bool PreservesRaw(IReadOnlyList<IntVector2> hull, IntRect rect)
    {
        int n = hull.Count;
        if (n < 3) return false;
        foreach (var v in hull) if (rect.Left <= v.X && v.X <= rect.Right && rect.Top <= v.Y && v.Y <= rect.Bottom) return false;
        bool In(long x, long y) { IntVector2 a = hull[n - 1]; foreach (var b in hull) { if (((long)b.X - a.X) * (y - a.Y) - ((long)b.Y - a.Y) * (x - a.X) < 0) return false; a = b; } return true; }
        return In(rect.Left, rect.Top) && In(rect.Right, rect.Top) && In(rect.Left, rect.Bottom) && In(rect.Right, rect.Bottom);
    }

    // ---- harness ----

    private static readonly AccessTools.FieldRef<List<Part>, int> PartsVersion = AccessTools.FieldRefAccess<List<Part>, int>("_version");
    private static readonly AccessTools.FieldRef<List<DestroyedPart>, int> DestroyedVersion = AccessTools.FieldRefAccess<List<DestroyedPart>, int>("_version");

    private static HullRemovalSkip.Pending Enter<TManager, TPart>(Ship<TManager, TPart> ship, TPart part, bool exactType)
        where TManager : CommonBasePartsManager<TPart> where TPart : CommonBasePart =>
        ship.Manager switch
        {
            CommonBasePartsManager<Part> m => HullRemovalSkip.Enter(m, (Part)(object)part, exactType, PartsVersion),
            CommonBasePartsManager<DestroyedPart> m => HullRemovalSkip.Enter(m, (DestroyedPart)(object)part, exactType, DestroyedVersion),
            _ => throw new NotSupportedException(),
        };

    private static void Exit<TManager, TPart>(Ship<TManager, TPart> ship, HullRemovalSkip.Pending state, Exception? exception)
        where TManager : CommonBasePartsManager<TPart> where TPart : CommonBasePart
    {
        switch (ship.Manager)
        {
            case CommonBasePartsManager<Part> m: HullRemovalSkip.Exit(m, state, exception, PartsVersion); break;
            case CommonBasePartsManager<DestroyedPart> m: HullRemovalSkip.Exit(m, state, exception, DestroyedVersion); break;
            default: throw new NotSupportedException();
        }
    }

    /// <summary>One removal through the hooks around vanilla's removal steps; true when the cache was refilled.</summary>
    private static bool Remove<TManager, TPart>(Ship<TManager, TPart> ship, int index)
        where TManager : CommonBasePartsManager<TPart> where TPart : CommonBasePart
    {
        HullRemovalSkip.Pending state = Enter(ship, ship.Parts[index], exactType: true);
        ship.NativeRemove(index);
        Exit(ship, state, null);
        Assert.Equal(state.Armed, ship.Manager._convexHull != null);
        return ship.Manager._convexHull != null;
    }

    /// <summary>
    /// Removes every part. After each removal the kept hull (if any) is compared with a vanilla rebuild; then the next
    /// removal sees either the kept hull, a fresh vanilla hull or none (a refresh not yet run), chosen at random.
    /// </summary>
    private static (int Kept, int Compared) Destroy<TManager, TPart>(Ship<TManager, TPart> ship, Random rand, Func<int> next)
        where TManager : CommonBasePartsManager<TPart> where TPart : CommonBasePart
    {
        int kept = 0, compared = 0;
        ship.Refresh();
        while (ship.Parts.Count > 0)
        {
            bool refilled = Remove(ship, next());
            if (refilled)
            {
                kept++;
                IntVector2[] mine = ship.Hull()!;
                (int, int, int) circle = ship.CircleBits();
                AssertVanilla(ship, mine, circle);
                compared++;
                switch (rand.Next(3))
                {
                    case 0: ship.SetHull(mine); break; // next removal checks against the kept hull
                    case 1: break;                     // vanilla rebuild in place
                    default: ship.ClearHull(); break;  // dirty: next removal finds no hull
                }
            }
            else if (rand.Next(2) == 0)
            {
                ship.Refresh();
            }
        }
        return (kept, compared);
    }

    /// <summary>Rebuilds the hull with vanilla code (leaves it cached) and compares ordered points and circle bits.</summary>
    private static void AssertVanilla<TManager, TPart>(Ship<TManager, TPart> ship, IntVector2[]? expected, (int, int, int)? circle = null)
        where TManager : CommonBasePartsManager<TPart> where TPart : CommonBasePart
    {
        ship.ClearHull();
        ship.Refresh();
        if (expected != null) Assert.Equal(expected, ship.Hull());
        if (circle != null) Assert.Equal(circle.Value, ship.CircleBits());
    }

    private sealed class Ship<TManager, TPart> where TManager : CommonBasePartsManager<TPart> where TPart : CommonBasePart
    {
        private static readonly MethodInfo RefreshMethod = AccessTools.Method(typeof(CommonBasePartsManager<TPart>), "RefreshConvexHull");
        private static readonly MethodInfo CircleMethod = AccessTools.Method(typeof(CommonBasePartsManager<TPart>), "CalculateBoundingCircle");
        internal readonly TManager Manager = Shell<TManager>();
        internal readonly List<TPart> Parts = [];
        private readonly Dictionary<IntVector2, TPart> _cells = [];

        private Ship()
        {
            Set(Manager, "_orderedParts", Parts);
            Set(Manager, "_partsByCell", _cells);
        }

        private void Add(PartRules rules, IntVector2 location, int rotFlip)
        {
            var part = Shell<TPart>();
            Set(part, "<Rules>k__BackingField", rules);
            Set(part, "<Location>k__BackingField", location);
            Set(part, "_rotFlip", rotFlip);
            int index = ~Parts.BinarySearch(part, PartComparer<TPart>.Instance);
            Parts.Insert(index, part);
            foreach (IntVector2 cell in part.GetCells()) _cells.Add(cell, part);
        }

        private static PartRules Rules(int w, int h)
        {
            var r = Shell<PartRules>();
            r.Size = new IntVector2(w, h);
            return r;
        }

        internal static Ship<TManager, TPart> Random(int seed)
        {
            var ship = new Ship<TManager, TPart>();
            var rand = new Random(seed);
            var rules = new List<PartRules>();
            for (int i = 0; i < 12; i++)
            {
                PartRules r = Rules(1 + rand.Next(3), 1 + rand.Next(3));
                if (rand.Next(3) == 0)
                {
                    int w = 1 + rand.Next(r.Size.X), h = 1 + rand.Next(r.Size.Y);
                    r.PhysicalRect = new IntRect(rand.Next(r.Size.X - w + 1), rand.Next(r.Size.Y - h + 1), w, h);
                    r.BoundingRectType = rand.Next(4) == 0 ? PartRectType.Normal : PartRectType.Physical;
                }
                rules.Add(r);
            }
            for (int y = 0; y < 24; y++)
                for (int x = 0; x < 24; x++)
                {
                    if (y is 11 or 12 || rand.Next(5) == 0) continue;
                    PartRules r = rules[rand.Next(rules.Count)];
                    int rot = rand.Next(4);
                    IntRect rect = r.GetRect(new IntVector2(x, y), rot);
                    if (rect.Right > 24 || rect.Bottom > 24 || (y < 11 && rect.Bottom > 11) || rect.Any(ship._cells.ContainsKey)) continue;
                    ship.Add(r, new IntVector2(x, y), rot | (rand.Next(2) == 0 ? 4 : 0));
                }
            return ship;
        }

        internal static Ship<TManager, TPart> Cells(IEnumerable<IntVector2> cells)
        {
            var ship = new Ship<TManager, TPart>();
            PartRules r = Rules(1, 1);
            foreach (IntVector2 cell in cells) ship.Add(r, cell, 0);
            return ship;
        }

        internal static Ship<TManager, TPart> Block(int width, int height, int w, int h)
        {
            var ship = new Ship<TManager, TPart>();
            PartRules r = Rules(w, h);
            for (int y = 0; y + h <= height; y += h)
                for (int x = 0; x + w <= width; x += w)
                    ship.Add(r, new IntVector2(x, y), 0);
            return ship;
        }

        internal static Ship<TManager, TPart> Pattern(int width, int height, Func<int, int, bool> filled, int ox = 0, int oy = 0)
        {
            var ship = new Ship<TManager, TPart>();
            PartRules r = Rules(1, 1);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (filled(x, y)) ship.Add(r, new IntVector2(ox + x, oy + y), 0);
            return ship;
        }

        /// <summary>Vanilla RemovePart's effect on the hull inputs and cache (cells, ordered list, hull discarded).</summary>
        internal void NativeRemove(int index)
        {
            foreach (IntVector2 cell in Parts[index].GetCells()) _cells.Remove(cell);
            Parts.RemoveAt(index);
            Manager._convexHull?.Dispose();
            Manager._convexHull = null;
        }

        internal void Refresh() => Invoke(RefreshMethod);

        internal void ClearHull()
        {
            Manager._convexHull?.Dispose();
            Manager._convexHull = null;
        }

        internal void SetHull(IntVector2[] points)
        {
            ClearHull();
            TempList<IntVector2> hull = TempList<IntVector2>.Alloc();
            foreach (IntVector2 p in points) hull.Add(p);
            Manager._convexHull = hull;
        }

        internal IntVector2[]? Hull() => Manager._convexHull?.ToArray();

        internal (int, int, int) CircleBits()
        {
            var circle = (Circle?)Invoke(CircleMethod);
            Assert.NotNull(circle);
            Circle c = circle.Value;
            return (BitConverter.SingleToInt32Bits(c.Center.X), BitConverter.SingleToInt32Bits(c.Center.Y), BitConverter.SingleToInt32Bits(c.Radius));
        }

        private object? Invoke(MethodInfo method)
        {
            try
            {
                return method.Invoke(Manager, null);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }

    private static void Set(object instance, string name, object? value)
    {
        for (Type? type = instance.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo? field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field == null) continue;
            field.SetValue(instance, value);
            return;
        }
        throw new MissingFieldException(instance.GetType().FullName, name);
    }

    private static T Shell<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
}
