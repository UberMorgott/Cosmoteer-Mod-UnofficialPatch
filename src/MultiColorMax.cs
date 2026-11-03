using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Ships.Parts.Graphics;
using Halfling.Geometry;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>RGB Max seeds its reduction with negative infinity but vanilla calls Min; replace that callback's operation.</summary>
internal static class MultiColorMax
{
    private const string ParentIlSha256 = "2F926DB1D4D93130F341D86F1A48C377C3FD70882B8072C2682184718E4144F7";
    private const string IlSha256 = "0A679F1D10216AC2977F123AA1012FA874875B78757F9029D9AD882019128012";

    internal static MethodInfo Parent => AccessTools.Method(typeof(PartMultiColor), "OnColorChanged")
        ?? throw new MissingMethodException(nameof(PartMultiColor), "OnColorChanged");

    internal static MethodInfo Target => FindTarget(PatchProcessor.GetOriginalInstructions(Parent));

    /// <summary>Pin the caller too: the unchanged Min callback body alone cannot prove it still belongs to the Max arm.</summary>
    internal static MethodInfo FindTarget(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = Patcher.RequireIl(instructions, ParentIlSha256, "PartMultiColor.OnColorChanged");
        int seed = code.FindIndex(c => c.opcode == OpCodes.Ldc_R4 && c.operand is float f && float.IsNegativeInfinity(f));
        return code.Skip(seed + 1).First(c => c.opcode == OpCodes.Ldftn).operand as MethodInfo
            ?? throw new MissingMethodException(nameof(PartMultiColor), "RGB Max callback");
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        _ = Target; // verify the caller/callback association even when invoked outside Patcher
        List<CodeInstruction> code = Patcher.RequireIl(instructions, IlSha256, "PartMultiColor.RGB Max callback");
        Type byRef = typeof(Vector3).MakeByRefType();
        MethodInfo min = AccessTools.Method(typeof(Vector3), nameof(Vector3.Min), [byRef, byRef]);
        MethodInfo max = AccessTools.Method(typeof(Vector3), nameof(Vector3.Max), [byRef, byRef]);
        code.Single(c => c.Calls(min)).operand = max;
        return code;
    }
}
