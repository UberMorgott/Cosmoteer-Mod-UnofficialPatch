using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Ships.Parts.Thrusters;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Vanilla ThrusterManager.CalculateMaximumAccelerationAndRampTimeCached, on a cache miss, builds a
/// Dictionary&lt;Thruster, float&gt; of every thruster's UncomittedActivationLevel and then keeps it only for the few
/// directions in s_allowedCachedDirections; for every other direction it is garbage (~7 MB/s in an MP profile).
/// The transpiler moves the dictionary build into the existing allowed-direction branch and presizes it to the thruster
/// count. Skipped work only reads UncomittedActivationLevel (an auto-property getter) into the throwaway dictionary;
/// CalculateMaximumAcceleration and CalculateRampTimes run as before, so results are bit-identical (lockstep-safe).
/// </summary>
internal static class ThrusterAccelerationCache
{
    private const string IlSha256 = "D64E121EF2E4C9FB1EFA4D65E57B20E69AF89833D718CE9D1B5ACE803D5CA0F1";

    internal static MethodInfo Target =>
        AccessTools.Method(typeof(ThrusterManager), nameof(ThrusterManager.CalculateMaximumAccelerationAndRampTimeCached))
        ?? throw new MissingMethodException(nameof(ThrusterManager), nameof(ThrusterManager.CalculateMaximumAccelerationAndRampTimeCached));

    /// <summary>
    /// Miss branch, vanilla order: [38..46) Item1 = CalculateMaximumAcceleration, [46..71) Item2 = new Dictionary + fill
    /// loop (try/finally), [71..75) if (s_allowedCachedDirections.Contains(direction)), [75..80) _cachedAccelerations.Add.
    /// New order: Item1, if, Item2 = new Dictionary(_orderedThrusters.Count) + fill, Add.
    /// </summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = Patcher.RequireIl(instructions, IlSha256, "ThrusterManager.CalculateMaximumAccelerationAndRampTimeCached");
        // The fill loop's leave targets the instruction after it (the if); it now has to land on the Add.
        code[75].labels.AddRange(code[71].labels);
        code[71].labels.Clear();
        List<CodeInstruction> fill = code.GetRange(46, 25);
        fill[1] = new CodeInstruction(OpCodes.Newobj, typeof(Dictionary<Thruster, float>).GetConstructor([typeof(int)]));
        fill.InsertRange(1,
        [
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(ThrusterManager), nameof(ThrusterManager._orderedThrusters))),
            new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(List<Thruster>), nameof(List<Thruster>.Count))),
        ]);
        var result = new List<CodeInstruction>(code.Count + 3);
        result.AddRange(code.GetRange(0, 46));
        result.AddRange(code.GetRange(71, 4));
        result.AddRange(fill);
        result.AddRange(code.GetRange(75, code.Count - 75));
        return result;
    }
}
