using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships.Blueprints;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Repairs;
using Halfling.Geometry;
using HarmonyLib;
using Xunit;

namespace UnofficialPatch.Tests;

[Collection(nameof(BoundingPointsTests))]
public sealed class BoundingPointsTests
{
    private const string TestId = "morgott.unofficialpatch.tests.boundingpoints";

    [Fact]
    public void KillSwitchIsOnlyZero()
    {
        Assert.False(BoundingPoints.IsRequested("0"));
        Assert.True(BoundingPoints.IsRequested(null));
        Assert.True(BoundingPoints.IsRequested("1"));
    }

    /// <summary>Every game instantiation shares the patched reference-type code.</summary>
    [Fact]
    public void GameInstantiationsAreReferenceTypes()
    {
        Type[] managers = typeof(CommonBasePartsManager<>).Assembly.GetTypes()
            .Select(t => t.BaseType)
            .Where(b => b is { IsGenericType: true } && b.GetGenericTypeDefinition() == typeof(CommonBasePartsManager<>))
            .Distinct().OrderBy(t => t!.FullName).ToArray()!;
        Assert.Equal([typeof(CommonBasePartsManager<BlueprintPart>), typeof(CommonBasePartsManager<Part>), typeof(CommonBasePartsManager<DestroyedPart>)], managers);
        Assert.All(managers, m => Assert.False(m.GetGenericArguments()[0].IsValueType));
    }

    [Fact]
    public void TranspilerMatchesRealGameIl()
    {
        List<CodeInstruction> code = BoundingPoints.Transpiler(PatchProcessor.GetOriginalInstructions(BoundingPoints.Target), BoundingPoints.Target).ToList();
        Assert.Equal(4, code.Count);
        Assert.True(code[2].Calls(AccessTools.Method(typeof(BoundingPoints), nameof(BoundingPoints.Fill)).MakeGenericMethod(typeof(Part))));
        Assert.Equal(OpCodes.Ret, code[3].opcode);
    }

    [Fact]
    public void TranspilerRejectsChangedIl()
    {
        List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(BoundingPoints.Target);
        code.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => BoundingPoints.Transpiler(code, BoundingPoints.Target).ToList());
    }

    /// <summary>
    /// Random ships of rotated, flipped, padded (physical rect) parts with holes, split into islands and destroyed part by
    /// part: the replacement emits the exact vanilla point sequence after every change, for both instantiations.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void FillEmitsVanillaSequenceThroughDestruction(int seed)
    {
        var part = new Ship<PartsManager, Part>(seed);
        var blueprint = new Ship<BlueprintPartsManager, BlueprintPart>(seed);
        var rand = new Random(seed * 7919);
        int compared = 0;
        while (part.Parts.Count > 0)
        {
            List<IntVector2> vanilla = VanillaPart(part.Manager);
            Assert.Equal(vanilla, Fill(part.Manager));
            Assert.Equal(VanillaBlueprint(blueprint.Manager), Fill(blueprint.Manager));
            compared += vanilla.Count;
            int index = rand.Next(part.Parts.Count);
            part.RemoveAt(index);
            blueprint.RemoveAt(index);
        }
        Assert.InRange(compared, 1000, int.MaxValue);
    }

    /// <summary>The installed patch serves both managers (shared code) with vanilla output.</summary>
    [Fact]
    public void PatchedMethodEmitsVanillaSequenceForBothInstantiations()
    {
        var part = new Ship<PartsManager, Part>(11);
        var blueprint = new Ship<BlueprintPartsManager, BlueprintPart>(11);
        List<IntVector2> vanillaPart = VanillaPart(part.Manager), vanillaBlueprint = VanillaBlueprint(blueprint.Manager);
        Assert.NotEmpty(vanillaPart);
        var harmony = new Harmony(TestId);
        try
        {
            harmony.Patch(BoundingPoints.Target, transpiler: new HarmonyMethod(typeof(BoundingPoints), nameof(BoundingPoints.Transpiler)));
            Assert.Equal(vanillaPart, Call(BoundingPoints.Target, part.Manager));
            Assert.Equal(vanillaBlueprint, Call(AccessTools.Method(typeof(CommonBasePartsManager<BlueprintPart>), "GetBoundingPoints"), blueprint.Manager));
        }
        finally
        {
            harmony.UnpatchAll(TestId);
        }
    }

    /// <summary>An unknown rect type throws as in vanilla.</summary>
    [Fact]
    public void UnknownRectTypeThrowsLikeVanilla()
    {
        var ship = new Ship<PartsManager, Part>(5);
        ship.Parts[^1].Rules.BoundingRectType = (PartRectType)7;
        Assert.Throws<NotSupportedException>(() => VanillaPart(ship.Manager));
        Assert.Throws<NotSupportedException>(() => Fill(ship.Manager));
    }

    private static List<IntVector2> Fill<TPart>(CommonBasePartsManager<TPart> manager) where TPart : CommonBasePart
    {
        var points = new List<IntVector2>();
        BoundingPoints.Fill(manager, points);
        return points;
    }

    private static List<IntVector2> Call(MethodInfo method, object manager)
    {
        var points = new List<IntVector2>();
        try
        {
            method.Invoke(manager, [points]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
        return points;
    }

    // Reference: the original vanilla IL, independent of any patch installed meanwhile.
    private static List<IntVector2> VanillaPart(CommonBasePartsManager<Part> manager)
    {
        var points = new List<IntVector2>();
        Reverse.Part(manager, points);
        return points;
    }

    private static List<IntVector2> VanillaBlueprint(CommonBasePartsManager<BlueprintPart> manager)
    {
        var points = new List<IntVector2>();
        Reverse.Blueprint(manager, points);
        return points;
    }

    /// <summary>Stubs replaced once by copies of the original IL (reverse patches), so later patches never reach them.</summary>
    private static class Reverse
    {
        private static readonly object Gate = new();
        private static bool _ready;

        internal static void Part(CommonBasePartsManager<Part> instance, List<IntVector2> points)
        {
            Ensure();
            PartStub(instance, points);
        }

        internal static void Blueprint(CommonBasePartsManager<BlueprintPart> instance, List<IntVector2> points)
        {
            Ensure();
            BlueprintStub(instance, points);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PartStub(CommonBasePartsManager<Part> instance, List<IntVector2> points) => throw new NotImplementedException("stub");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void BlueprintStub(CommonBasePartsManager<BlueprintPart> instance, List<IntVector2> points) => throw new NotImplementedException("stub");

        private static void Ensure()
        {
            lock (Gate)
            {
                if (_ready) return;
                var harmony = new Harmony(TestId + ".reverse");
                harmony.CreateReversePatcher(BoundingPoints.Target, new HarmonyMethod(typeof(Reverse), nameof(PartStub))).Patch(HarmonyReversePatchType.Original);
                harmony.CreateReversePatcher(AccessTools.Method(typeof(CommonBasePartsManager<BlueprintPart>), "GetBoundingPoints"),
                    new HarmonyMethod(typeof(Reverse), nameof(BlueprintStub))).Patch(HarmonyReversePatchType.Original);
                _ready = true;
            }
        }
    }

    /// <summary>
    /// A ship of 1x1..3x3 parts on a 24x24 grid: random rotation and flip, some with a physical rect smaller than the part
    /// and bounding by it, random empty cells (holes) and an empty band through the middle (two islands).
    /// </summary>
    private sealed class Ship<TManager, TPart> where TManager : CommonBasePartsManager<TPart> where TPart : CommonBasePart
    {
        internal readonly TManager Manager = Shell<TManager>();
        internal readonly List<TPart> Parts = [];
        private readonly Dictionary<IntVector2, TPart> _cells = [];

        internal Ship(int seed)
        {
            var rand = new Random(seed);
            var rules = new List<PartRules>();
            for (int i = 0; i < 12; i++)
            {
                var r = Shell<PartRules>();
                r.Size = new IntVector2(1 + rand.Next(3), 1 + rand.Next(3));
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
                    if (rect.Right > 24 || rect.Bottom > 24 || (y < 11 && rect.Bottom > 11) || rect.Any(_cells.ContainsKey)) continue;
                    var part = Shell<TPart>();
                    Set(part, "<Rules>k__BackingField", r);
                    Set(part, "<Location>k__BackingField", new IntVector2(x, y));
                    Set(part, "_rotFlip", rot | (rand.Next(2) == 0 ? 4 : 0));
                    Parts.Add(part);
                    foreach (IntVector2 cell in part.GetCells()) _cells.Add(cell, part);
                }
            Set(Manager, "_orderedParts", Parts);
            Set(Manager, "_partsByCell", _cells);
        }

        internal void RemoveAt(int index)
        {
            foreach (IntVector2 cell in Parts[index].GetCells()) _cells.Remove(cell);
            Parts.RemoveAt(index);
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
