using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Ships.Blueprints;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Logic;
using Cosmoteer.Source.Ships.Blueprints;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Vanilla BaseBlueprintPartNetworkPort.UpdateOperational runs per port every frame and allocates
/// <c>new Func(this.GetToggleMode)</c> each time (~24% of all allocations in an MP profile). The transpiler swaps only that
/// ldftn/newobj pair for <see cref="Get"/>: one delegate per port, bound once. GetToggleMode reads live part/ship state on
/// every call, so caching the binding (not results) needs no invalidation and keeps the sim identical.
/// </summary>
internal static class PortToggleMode
{
    // Weak keys: a removed port and its delegate are collected together.
    private static readonly ConditionalWeakTable<BaseBlueprintPartNetworkPort, Func<ID<PartToggleGuiRules>, int?>> Cache = new();

    public static Func<ID<PartToggleGuiRules>, int?> Get(BaseBlueprintPartNetworkPort port) =>
        Cache.GetValue(port, static p => p.GetToggleMode);

    /// <summary>Whole vanilla body must match exactly (else throw: Patcher rolls back to vanilla); then ldftn+newobj -> call Get.</summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        Type port = typeof(BaseBlueprintPartNetworkPort);
        Type func = typeof(Func<ID<PartToggleGuiRules>, int?>);
        var expected = new (OpCode Op, MemberInfo? Operand)[]
        {
            (OpCodes.Ldarg_0, null),
            (OpCodes.Ldarg_0, null),
            (OpCodes.Ldfld, AccessTools.Field(port, nameof(BaseBlueprintPartNetworkPort._rulesOperationalToggle))),
            (OpCodes.Ldarg_0, null),
            (OpCodes.Call, AccessTools.PropertyGetter(typeof(BlueprintPartComponent), nameof(BlueprintPartComponent.Part))),
            (OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(CommonBasePart), nameof(CommonBasePart.Rules))),
            (OpCodes.Ldarg_0, null),
            (OpCodes.Ldftn, AccessTools.Method(port, nameof(BaseBlueprintPartNetworkPort.GetToggleMode))),
            (OpCodes.Newobj, func.GetConstructor(new[] { typeof(object), typeof(IntPtr) })),
            (OpCodes.Callvirt, AccessTools.Method(typeof(IBlueprintComponentToggle), nameof(IBlueprintComponentToggle.IsBlueprintToggleOn))),
            (OpCodes.Call, AccessTools.Method(port, nameof(BaseBlueprintPartNetworkPort.SetOperational))),
            (OpCodes.Ret, null),
        };
        List<CodeInstruction> code = instructions.ToList();
        bool matches = code.Count == expected.Length
            && code.Zip(expected).All(p => p.First.opcode == p.Second.Op && p.First.labels.Count == 0 && p.First.blocks.Count == 0
                && SameOperand(p.First.operand, p.Second.Operand));
        if (!matches)
        {
            throw new InvalidOperationException("BaseBlueprintPartNetworkPort.UpdateOperational IL changed:\n  " + string.Join("\n  ", code));
        }
        code[7] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PortToggleMode), nameof(Get)));
        code.RemoveAt(8);
        return code;
    }

    /// <summary>Same metadata member (and generic instantiation); reflection does not promise one MemberInfo object per member.</summary>
    private static bool SameOperand(object? actual, MemberInfo? expected) =>
        expected == null
            ? actual == null
            : actual is MemberInfo m && m.Module == expected.Module && m.MetadataToken == expected.MetadataToken && m.DeclaringType == expected.DeclaringType;
}
