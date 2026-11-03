using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Data;
using Cosmoteer.Nebulas;
using Cosmoteer.Ships;
using Halfling.Geometry;
using Halfling.Pooling;
using HarmonyLib;
using Xunit;

namespace UnofficialPatch.Tests;

public sealed class NebulaTempListTests
{
    private const string TestId = "morgott.unofficialpatch.tests.nebula";

    [Fact]
    public void KillSwitchIsOnlyZero()
    {
        Assert.False(NebulaTempList.IsRequested("0"));
        Assert.True(NebulaTempList.IsRequested(null));
        Assert.True(NebulaTempList.IsRequested("1"));
    }

    [Fact]
    public void TranspilersMatchRealGameIl()
    {
        List<CodeInstruction> fill = NebulaTempList.Transpiler(PatchProcessor.GetOriginalInstructions(NebulaTempList.Target)).ToList();
        Assert.Equal(5, fill.Count);
        Assert.True(fill[3].Calls(AccessTools.Method(typeof(NebulaTempList), nameof(NebulaTempList.Fill))));

        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(NebulaTempList.EarlyOut.RefreshTarget);
        List<CodeInstruction> refresh = NebulaTempList.EarlyOut.Transpiler(original, new DynamicMethod("x", null, null).GetILGenerator()).ToList();
        Assert.Equal(original.Count + 5, refresh.Count);
        Assert.True(refresh[2].Calls(AccessTools.Method(typeof(NebulaTempList.EarlyOut), nameof(NebulaTempList.EarlyOut.Skip))));
        Assert.Equal(OpCodes.Ret, refresh[4].opcode);
    }

    [Fact]
    public void TranspilersRejectChangedIl()
    {
        List<CodeInstruction> fill = PatchProcessor.GetOriginalInstructions(NebulaTempList.Target);
        fill.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => NebulaTempList.Transpiler(fill).ToList());

        List<CodeInstruction> refresh = PatchProcessor.GetOriginalInstructions(NebulaTempList.EarlyOut.RefreshTarget);
        refresh.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() =>
            NebulaTempList.EarlyOut.Transpiler(refresh, new DynamicMethod("x", null, null).GetILGenerator()).ToList());
    }

    [Fact]
    public void SkipsOnlyWithoutGridCellsAndTrackedNebulas()
    {
        SimNebulasManager nebulas = Manager();
        Ship ship = ShipWithNebulas(0);
        Assert.True(NebulaTempList.EarlyOut.Skip(nebulas, ship));
        Assert.False(NebulaTempList.EarlyOut.Skip(nebulas, null));
        Assert.False(NebulaTempList.EarlyOut.Skip(nebulas, ShipWithNebulas(1)));
        nebulas._grid[default] = (ID<NebulaTypeRules>)1;
        Assert.False(NebulaTempList.EarlyOut.Skip(nebulas, ship));
    }

    /// <summary>Same points in the same order reach the point lookup as in vanilla; only the patched list goes back to the pool.</summary>
    [Fact]
    public void FillPassesVanillaPointsAndRecyclesList()
    {
        var stubs = new Harmony(TestId + ".stubs");
        var patch = new Harmony(TestId);
        try
        {
            stubs.Patch(AccessTools.Method(typeof(Ship), nameof(Ship.DetTransformPointToWorld), [typeof(Vector2)]),
                prefix: new HarmonyMethod(typeof(NebulaTempListTests), nameof(TransformStub)));
            stubs.Patch(AccessTools.Method(typeof(SimNebulasManager), nameof(SimNebulasManager.GetInsideTypes),
                    [typeof(IReadOnlyList<Vector2>), typeof(ICollection<NebulaStrengthInfo>), typeof(NebulaFeatherType), typeof(float), typeof(bool)]),
                prefix: new HarmonyMethod(typeof(NebulaTempListTests), nameof(LookupStub)));

            Ship ship = ShipWithHull([new(3, -2), new(-7, 5), new(0, 0), new(11, 4), new(-1, -9), new(6, 6)]);
            SimNebulasManager nebulas = Manager();

            string vanilla = Run(nebulas, ship, out TempList<Vector2> vanillaList);
            Assert.NotEmpty(vanillaList); // vanilla leaks the list with its points
            patch.Patch(NebulaTempList.Target, transpiler: new HarmonyMethod(typeof(NebulaTempList), nameof(NebulaTempList.Transpiler)));
            string patched = Run(nebulas, ship, out TempList<Vector2> patchedList);

            Assert.Equal(vanilla, patched);
            Assert.Empty(patchedList); // recycled (pool deinitializer clears it)
            Assert.True(patchedList.Capacity >= 6);
        }
        finally
        {
            patch.UnpatchAll(TestId);
            stubs.UnpatchAll(TestId + ".stubs");
        }
    }

    [ThreadStatic] private static List<string>? _calls;
    [ThreadStatic] private static TempList<Vector2>? _seen;

    private static string Run(SimNebulasManager nebulas, Ship ship, out TempList<Vector2> list)
    {
        _calls = [];
        var strengths = new List<NebulaStrengthInfo>();
        NebulaTempList.Target.Invoke(nebulas, [ship, strengths]);
        list = _seen!;
        _calls.Add("strengths " + string.Join(",", strengths.Select(s => ((int)s.Type).ToString() + ":" + BitConverter.SingleToInt32Bits(s.Strength).ToString("X8"))));
        return string.Join("\n", _calls);
    }

    public static bool TransformStub(Vector2 point, ref Vector2 __result)
    {
        __result = new Vector2(point.X * 1.5f + 0.25f, point.Y * -0.75f + 2f);
        return false;
    }

    public static bool LookupStub(IReadOnlyList<Vector2> worldPoints, ICollection<NebulaStrengthInfo> strengths, NebulaFeatherType featherType, float buffer, bool onlyIfExplored)
    {
        _seen = (TempList<Vector2>)worldPoints;
        _calls!.Add($"lookup {featherType} {BitConverter.SingleToInt32Bits(buffer):X8} {onlyIfExplored}: "
            + string.Join(";", worldPoints.Select(p => BitConverter.SingleToInt32Bits(p.X).ToString("X8") + "," + BitConverter.SingleToInt32Bits(p.Y).ToString("X8"))));
        strengths.Add(new NebulaStrengthInfo((ID<NebulaTypeRules>)worldPoints.Count, 0.5f, default));
        return false;
    }

    private static SimNebulasManager Manager()
    {
        var nebulas = (SimNebulasManager)RuntimeHelpers.GetUninitializedObject(typeof(SimNebulasManager));
        AccessTools.Field(typeof(SimNebulasManager), nameof(SimNebulasManager._grid)).SetValue(nebulas, new Dictionary<TriCoord, ID<NebulaTypeRules>>());
        return nebulas;
    }

    private static Ship ShipWithNebulas(int count)
    {
        var ship = (Ship)RuntimeHelpers.GetUninitializedObject(typeof(Ship));
        var tracker = (ShipNebulasTracker)RuntimeHelpers.GetUninitializedObject(typeof(ShipNebulasTracker));
        FieldInfo current = AccessTools.Field(typeof(ShipNebulasTracker), "_currentNebulas");
        object list = Activator.CreateInstance(current.FieldType)!;
        for (int i = 0; i < count; i++)
            current.FieldType.GetMethod("Add")!.Invoke(list, [Activator.CreateInstance(current.FieldType.GetGenericArguments()[0])]);
        current.SetValue(tracker, list);
        AccessTools.Field(typeof(Ship), "<Nebulas>k__BackingField").SetValue(ship, tracker);
        return ship;
    }

    private static Ship ShipWithHull(IntVector2[] hull)
    {
        var ship = (Ship)RuntimeHelpers.GetUninitializedObject(typeof(Ship));
        object parts = RuntimeHelpers.GetUninitializedObject(AccessTools.Field(typeof(Ship), "<Parts>k__BackingField").FieldType);
        TempList<IntVector2> points = TempList<IntVector2>.Alloc();
        points.AddRange(hull);
        AccessTools.Field(parts.GetType(), "_convexHull").SetValue(parts, points);
        AccessTools.Field(typeof(Ship), "<Parts>k__BackingField").SetValue(ship, parts);
        return ship;
    }
}
