using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Statuses;
using Halfling.Geometry;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Every sim tick, every status handler with value modulators (fire, damage-over-time and similar statuses on tiles and
/// parts) runs StatusHandler.FixedUpdate → _ModulateStatusValues, which takes a pooled TempList of changed statuses and a
/// pooled TempDictionary of the statuses at the current location and never returns either, so the pools run dry and both
/// become garbage on every tick of every such handler (the top combat allocation site on the sim workers).
/// <para/>
/// The vanilla body stays as it is; it is only wrapped in two nested try/finally blocks that give the dictionary and then
/// the list back to the pool after the deferred OnStatusValueModified loop has finished (also when it throws). Both
/// collections are only read inside the method (the dictionary through ValueModulationData, which is not kept), so the
/// statuses, their order, the float math, the value assignments and the notification order are unchanged. Patched for
/// both instantiations the game uses: StatusHandler&lt;IntVector2&gt; (TileStatusHandler) and StatusHandler&lt;Part&gt;
/// (PartStatusHandler, shared reference-type code). Off: <c>PERFORMANCE_STATUS_TEMP_DISPOSE=0</c>.
/// </summary>
internal static class StatusTempDispose
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_STATUS_TEMP_DISPOSE"));
    internal static bool IsRequested(string? value) => value != "0";

    private const string MethodName = "<Halfling.Scene2D.IFixedUpdateableSceneObject.FixedUpdate>g___ModulateStatusValues|65_0";

    internal static readonly (MethodInfo Target, string IlSha256)[] Targets =
    [
        (Find(typeof(StatusHandler<IntVector2>)), "A883BA4EF9CEA7BD18F35BF16D540400502356B3299B7DD51699FBDF96341E2B"),
        (Find(typeof(StatusHandler<Part>)), "3CFFA67BE1076D410A54BBD51ED30A3AB0C697BFBA9A4B48685AA37CDB48FCE7"),
    ];

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? Targets.Select(t => ((MethodBase)t.Target, typeof(StatusTempDispose))).ToArray() : [];

    private static MethodInfo Find(Type handler) => AccessTools.Method(handler, MethodName)
        ?? throw new MissingMethodException(handler.FullName, MethodName);

    /// <summary>
    /// <c>list = Alloc(); try { dict = Alloc(); try { vanilla rest } finally { dict.Dispose(); } } finally { list.Dispose(); }</c>.
    /// The vanilla early return before both allocations and every instruction of the rest stay unchanged.
    /// </summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original, ILGenerator generator)
    {
        string sha = Targets.Single(t => t.Target.DeclaringType == original.DeclaringType).IlSha256;
        List<CodeInstruction> code = Patcher.RequireIl(instructions, sha, original.DeclaringType!.Name + "." + MethodName);
        // Pinned shape: [5] call TempList.Alloc, [6] stloc.0, [7] call TempDictionary.Alloc, [8] stloc.1, ..., [^1] ret.
        Type list = ((MethodInfo)code[5].operand).ReturnType;
        Type dict = ((MethodInfo)code[7].operand).ReturnType;
        code[7].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
        code[9].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
        CodeInstruction ret = code[^1];
        Label end = generator.DefineLabel();
        code[^1] = new CodeInstruction(OpCodes.Leave, end).MoveLabelsFrom(ret);
        code.AddRange(
        [
            new CodeInstruction(OpCodes.Ldloc_1).WithBlocks(new ExceptionBlock(ExceptionBlockType.BeginFinallyBlock)),
            new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(dict, "Dispose")),
            new CodeInstruction(OpCodes.Endfinally).WithBlocks(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock)),
            new CodeInstruction(OpCodes.Leave, end),
            new CodeInstruction(OpCodes.Ldloc_0).WithBlocks(new ExceptionBlock(ExceptionBlockType.BeginFinallyBlock)),
            new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(list, "Dispose")),
            new CodeInstruction(OpCodes.Endfinally).WithBlocks(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock)),
            new CodeInstruction(OpCodes.Ret).WithLabels(end),
        ]);
        return code;
    }
}
