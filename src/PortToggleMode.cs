using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Data;
using Cosmoteer.Ships.Blueprints.Logic;
using Cosmoteer.Ships.Blueprints.Logic.Values;
using Cosmoteer.Game;
using Cosmoteer.Source.Ships.Blueprints;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Vanilla blueprint components that follow a rules toggle (network port, stat provider, network toggle) run an update
/// per component every frame that allocates <c>new Func(this.GetToggleMode)</c> each time (~24% + ~2% of all allocations
/// in an MP profile). The transpiler swaps only that ldftn/newobj pair for ldtoken/<see cref="Get"/>: one delegate per
/// component, bound once. GetToggleMode reads live part/ship state on every call, so caching the binding (not results)
/// needs no invalidation and keeps the sim identical.
/// </summary>
internal static class PortToggleMode
{
    // Weak keys: a removed component and its delegate are collected together. One binding per component: every patched
    // site binds the component's own GetToggleMode (pinned by the IL hashes below).
    private static readonly ConditionalWeakTable<object, Func<ID<PartToggleGuiRules>, int?>> Cache = new();

    /// <summary>Each patched method and the SHA-256 of its vanilla IL listing (see <see cref="Patcher.RequireIl"/>).</summary>
    internal static readonly (MethodInfo Target, string IlSha256)[] Targets =
    [
        (Method(typeof(BaseBlueprintPartNetworkPort), nameof(BaseBlueprintPartNetworkPort.UpdateOperational)), "61D234BBFD3504430AEE0DE10F121DDC3FF60E675B307B2CF0705B9A352C6AFB"),
        (Method(typeof(BlueprintPartStatProvider), nameof(BlueprintPartStatProvider.UpdateOperational)), "41B5773E5497AD8C06267AAD08099619F91A64945EB6BDB87C157A57799047E9"),
        (Method(typeof(BlueprintPartNetworkToggle), nameof(BlueprintPartNetworkToggle.UpdateToggleFromRules)), "93187D8079492BF5D1FD9AF11E0157A93DBEB5698386F92C15F99B586D316425"),
    ];

    private static MethodInfo Method(Type type, string name) =>
        AccessTools.Method(type, name) ?? throw new MissingMethodException(type.Name, name);

    /// <summary>Takes the stack of the replaced <c>newobj Func(object, IntPtr)</c>, with the method's token for its pointer.</summary>
    public static Func<ID<PartToggleGuiRules>, int?> Get(object owner, RuntimeMethodHandle getToggleMode)
    {
        if (!Cache.TryGetValue(owner, out Func<ID<PartToggleGuiRules>, int?>? f))
        {
            f = ((MethodInfo)MethodBase.GetMethodFromHandle(getToggleMode)!).CreateDelegate<Func<ID<PartToggleGuiRules>, int?>>(owner);
            Cache.AddOrUpdate(owner, f);
        }
        return f;
    }

    /// <summary>Whole vanilla body must match exactly (else throw: Patcher rolls back to vanilla); then ldftn+newobj -> ldtoken+call Get.</summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        (MethodInfo target, string sha) = Targets.Single(t => t.Target.MetadataToken == original.MetadataToken && t.Target.Module == original.Module);
        List<CodeInstruction> code = Patcher.RequireIl(instructions, sha, $"{target.DeclaringType!.Name}.{target.Name}");
        int i = code.FindIndex(c => c.opcode == OpCodes.Ldftn);
        code[i].opcode = OpCodes.Ldtoken;
        code[i + 1].opcode = OpCodes.Call;
        code[i + 1].operand = AccessTools.Method(typeof(PortToggleMode), nameof(Get));
        return code;
    }
}
