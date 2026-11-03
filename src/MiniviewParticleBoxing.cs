using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Simulation.MediaEffects;
using Halfling.Geometry;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>
/// Vanilla ParticleEffect's miniview emission check calls RotRect.IntersectsWith((IShape)node.WorldLocation), boxing a
/// Vector2 on every call (~62 MB per 20 s in an MP profile). Render only. That overload dispatches through
/// GeometryHelpers.IntersectsWith, whose first case (s is Vector2) ends in RotRect.Contains(in Vector2); the transpiler
/// calls Contains directly on the unboxed point, so the result is identical. All three dispatch methods are IL-pinned.
/// </summary>
internal static class MiniviewParticleBoxing
{
    private const string IlSha256 = "11B54CAFB8B23A22703935EA4104CC77AE8E134E66C74F6ADD9F7C7808A2CD0F";
    private const string DispatchIlSha256 = "861C4022A8071150A87506A21A33B940708E30284EEFA6336F1515AE0D2427EF";

    internal static MethodInfo Target =>
        AccessTools.Method(typeof(ParticleEffect), "<IsOnScreenForEmission>g___IsOnMiniview|12_1")
        ?? throw new MissingMethodException(nameof(ParticleEffect), "<IsOnScreenForEmission>g___IsOnMiniview|12_1");

    /// <summary>Vanilla path of RotRect.IntersectsWith(in IShape) for a boxed Vector2, outermost first.</summary>
    internal static MethodInfo?[] Dispatch =>
    [
        AccessTools.Method(typeof(RotRect), nameof(RotRect.IntersectsWith), [typeof(IShape).MakeByRefType()]),
        AccessTools.FirstMethod(typeof(GeometryHelpers), m => m.Name == nameof(GeometryHelpers.IntersectsWith) && m.IsGenericMethodDefinition
            && m.GetParameters() is [_, { ParameterType: var s }] && s == typeof(IShape))?.MakeGenericMethod(typeof(RotRect)),
        InterfaceTarget(typeof(IShapeMethods<Vector2>), nameof(IShapeMethods<Vector2>.IntersectsWith)),
    ];

    private static MethodInfo? InterfaceTarget(Type iface, string name)
    {
        InterfaceMapping map = typeof(RotRect).GetInterfaceMap(iface);
        int i = Array.FindIndex(map.InterfaceMethods, m => m.Name == name);
        return i < 0 ? null : map.TargetMethods[i];
    }

    /// <summary>Tail [box Vector2; stloc.s 5; ldloca.s 5; call RotRect.IntersectsWith(IShape&amp;)] becomes [call Contains].</summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var dispatch = new List<CodeInstruction>();
        foreach (MethodInfo? m in Dispatch)
        {
            dispatch.AddRange(PatchProcessor.GetOriginalInstructions(m ?? throw new MissingMethodException(nameof(RotRect), "IntersectsWith dispatch")));
        }
        Patcher.RequireIl(dispatch, DispatchIlSha256, "RotRect.IntersectsWith(IShape) dispatch");
        List<CodeInstruction> code = Patcher.RequireIl(instructions, IlSha256, "ParticleEffect._IsOnMiniview");
        int box = code.FindIndex(i => i.opcode == OpCodes.Box);
        code.RemoveRange(box, 3);
        code[box].operand = AccessTools.Method(typeof(MiniviewParticleBoxing), nameof(Contains)); // stays a call
        return code;
    }

    public static bool Contains(in RotRect rect, Vector2 point) => rect.Contains(in point);
}
