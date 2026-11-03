using System;
using System.Collections.Generic;
using System.Reflection;
using Halfling.Application;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>
/// Validates specialization after mod initialization, once per serialized native update.
/// The finalizer closes the interval even when native update throws. Installing patches during
/// an update interval is outside the specialization's compatibility contract.
/// </summary>
internal static class BlueprintEvaluationScope
{
    internal static readonly MethodInfo Target = AccessTools.Method(typeof(Director), "DoUpdate");

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        Patcher.RequireIl(instructions, "D3FBBDF29E3451CE39E664B66C8EDAED0A72BF230AE67B1294300DAFB3C755F2", "Director.DoUpdate");

    public static void Prefix()
    {
        Patcher.ApplyRetargetFallback(); // before the update interval: may unpatch, or throw (fatal) so the update never runs
        BlueprintToggleEvaluation.BeginValidatedUpdate();
    }

    public static Exception? Finalizer(Exception? __exception)
    {
        BlueprintToggleEvaluation.EndValidatedUpdate();
        return __exception;
    }
}
