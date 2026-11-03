using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships.Parts.Thrusters;
using Halfling.Geometry;
using Halfling.Timing;
using HarmonyLib;
using Xunit;

namespace UnofficialPatch.Tests;

public class ThrusterAccelerationCacheTests
{
    private const string TestId = "morgott.unofficialpatch.tests";
    private static readonly FieldInfo LevelField = AccessTools.Field(typeof(Thruster), "<UncomittedActivationLevel>k__BackingField");

    [Fact]
    public void TranspilerMatchesRealGameIl()
    {
        var original = PatchProcessor.GetOriginalInstructions(ThrusterAccelerationCache.Target);
        var patched = ThrusterAccelerationCache.Transpiler(original).ToList();

        int Index(string member, OpCode? op = null) =>
            patched.FindIndex(i => (op == null || i.opcode == op) && i.operand is MemberInfo m && m.Name == member);
        Assert.Equal(original.Count + 3, patched.Count);
        Assert.Single(patched, i => i.opcode == OpCodes.Newobj && i.operand is ConstructorInfo c
            && c.DeclaringType == typeof(Dictionary<Thruster, float>) && c.GetParameters().Single().ParameterType == typeof(int));
        // Accel first, then the allowed-direction check, then dictionary build + cache Add, then ramp times.
        Assert.True(Index("CalculateMaximumAcceleration") < Index("Contains"));
        Assert.True(Index("Contains") < Index(".ctor", OpCodes.Newobj));
        Assert.True(Index(".ctor", OpCodes.Newobj) < patched.FindLastIndex(i => i.operand is MethodInfo { Name: "Add" }));
        Assert.True(patched.FindLastIndex(i => i.operand is MethodInfo { Name: "Add" }) < Index("CalculateRampTimes"));
    }

    [Fact]
    public void TranspilerRejectsChangedIl()
    {
        var original = PatchProcessor.GetOriginalInstructions(ThrusterAccelerationCache.Target);
        original.Insert(0, new CodeInstruction(OpCodes.Nop));

        Assert.Throws<InvalidOperationException>(() => ThrusterAccelerationCache.Transpiler(original).ToList());
    }

    /// <summary>Same results, thruster state and cache contents (order included) as vanilla, for cached and uncached directions.</summary>
    [Fact]
    public void PatchedMatchesVanilla()
    {
        var stubs = new Harmony(TestId + ".stubs");
        var patch = new Harmony(TestId);
        try
        {
            // Headless stand-ins for the physics: deterministic levels and SRA from the requested direction.
            stubs.Patch(AccessTools.Method(typeof(ThrusterManager), nameof(ThrusterManager.CalculateDesiredSRA)),
                prefix: new HarmonyMethod(typeof(ThrusterAccelerationCacheTests), nameof(DesiredSraStub)));
            stubs.Patch(AccessTools.Method(typeof(ThrusterManager), nameof(ThrusterManager.CalculateRampTimes)),
                prefix: new HarmonyMethod(typeof(ThrusterAccelerationCacheTests), nameof(RampTimesStub)));

            Vector3[] directions = [new(1f, 0f, 0f), new(0.6f, 0.8f, 0f), new(0f, 0f, -1f), new(-0.28f, 0.96f, 0.5f)];
            string vanilla = Run(directions);
            patch.Patch(ThrusterAccelerationCache.Target, transpiler: new HarmonyMethod(typeof(ThrusterAccelerationCache), nameof(ThrusterAccelerationCache.Transpiler)));
            string patched = Run(directions);

            Assert.Contains("cache[", vanilla); // allowed directions really were cached
            Assert.Equal(vanilla, patched);
        }
        finally
        {
            patch.UnpatchAll(TestId);
            stubs.UnpatchAll(TestId + ".stubs");
        }
    }

    private static string Run(Vector3[] directions)
    {
        var manager = (ThrusterManager)RuntimeHelpers.GetUninitializedObject(typeof(ThrusterManager));
        var thrusters = Enumerable.Range(0, 5).Select(_ => (Thruster)RuntimeHelpers.GetUninitializedObject(typeof(Thruster))).ToList();
        AccessTools.Field(typeof(ThrusterManager), nameof(ThrusterManager._orderedThrusters)).SetValue(manager, thrusters);
        FieldInfo cacheField = AccessTools.Field(typeof(ThrusterManager), nameof(ThrusterManager._cachedAccelerations));
        cacheField.SetValue(manager, Activator.CreateInstance(cacheField.FieldType));

        var log = new List<string>();
        foreach (Vector3 d in directions)
        {
            var r = manager.CalculateMaximumAccelerationAndRampTimeCached(d, default(Time), new Vector3(1f, 2f, 3f), ThrusterManager.ActivationRangeType.Normal);
            log.Add($"{Bits(r.Accel)} {Bits(r.RampUpTime)} {Bits(r.RampDownTime)} levels {string.Join(",", thrusters.Select(t => Bits(t.UncomittedActivationLevel)))}");
        }
        foreach (var (key, value) in manager._cachedAccelerations)
        {
            log.Add($"cache[{key}] {Bits(value.Accel)} {string.Join(",", value.Activations.Select(a => $"{thrusters.IndexOf(a.Key)}={Bits(a.Value)}"))}");
        }
        return string.Join("\n", log);
    }

    private static string Bits(float f) => BitConverter.SingleToInt32Bits(f).ToString("X8");

    public static bool DesiredSraStub(ThrusterManager __instance, [HarmonyArgument("desiredSRA")] Vector3 sra, ref Vector3 __result)
    {
        List<Thruster> thrusters = __instance._orderedThrusters;
        for (int i = 0; i < thrusters.Count; i++)
        {
            LevelField.SetValue(thrusters[i], MathF.Sin(sra.X * 0.001f * (i + 1) + sra.Y * 0.002f - sra.Z * 0.003f * i));
        }
        __result = new Vector3(sra.Y, sra.X * 0.7f, sra.Z * 1.3f);
        return false;
    }

    public static bool RampTimesStub(ThrusterManager __instance, out float rampUpTime, out float rampDownTime)
    {
        rampUpTime = __instance._orderedThrusters.Sum(t => t.UncomittedActivationLevel);
        rampDownTime = __instance._orderedThrusters.Aggregate(1f, (p, t) => p * (1.5f + t.UncomittedActivationLevel));
        return false;
    }
}
