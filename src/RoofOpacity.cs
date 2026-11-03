using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Input;
using Cosmoteer.Ships.Rendering;
using Cosmoteer.Simulation;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>
/// Every rendered ship tests the cursor before checking the hide-roof key, repeating a spatial search and allocating
/// captured predicates even with the key released. Move the polled input check ahead of that read-only search; keep
/// forced/special roofs, toolbox, selection, interpolation and held-key behavior unchanged. Rendering only, no sim work.
/// </summary>
internal static class RoofOpacity
{
    private const string IlSha256 = "008045745253192700CF388A3226E5819150D0A9FC52BC4413799A620A2ED9F8";

    internal static MethodInfo Target => AccessTools.Method(typeof(ShipRenderer), "GetTargetRoofOpacity")
        ?? throw new MissingMethodException(nameof(ShipRenderer), "GetTargetRoofOpacity");

    // Reordering relies on the polled button and hit-test being reads. Refuse a game update that changes these bodies.
    private static readonly (Type Type, string Name, string Sha256)[] Dependencies =
    [
        (typeof(CustomInput), "get_IsDown", "296262C91BFC5C8BD5135D76D3B163837022E9BDAFE8D36BE032BA27A67922F9"),
        (typeof(PlayerInputHandler), "GetShipUnderCursor", "0B81A34C60F1CCEDFC54DF6C88669366D8E208CEF6FCAF9E6F6DA6A5C4AAC1D0"),
        (typeof(PlayerInputHandler), "GetShipsInRect", "BE6B00FAF99CED89B729EC2129A92FCB2ABEB5C93143B6F1AF17D3B9B7C36C36"),
    ];

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach ((Type type, string name, string sha) in Dependencies)
        {
            MethodInfo method = AccessTools.Method(type, name) ?? throw new MissingMethodException(type.Name, name);
            Patcher.RequireIl(PatchProcessor.GetOriginalInstructions(method), sha, $"{type.Name}.{name}");
        }
        List<CodeInstruction> code = Patcher.RequireIl(instructions, IlSha256, "ShipRenderer.GetTargetRoofOpacity");
        int key = code.FindIndex(c => c.opcode == OpCodes.Ldsfld && c.operand is FieldInfo { Name: "HideShipRoofUnderCursor" });
        int cursor = code.FindIndex(c => c.operand is MethodInfo { Name: nameof(PlayerInputHandler.GetShipUnderCursor) });
        // Move [ldsfld input; callvirt IsDown; brfalse fallback] before the complete cursor receiver/argument chain.
        List<CodeInstruction> check = code.GetRange(key, 3);
        code.RemoveRange(key, check.Count);
        code.InsertRange(cursor - 4, check);
        return code;
    }
}
