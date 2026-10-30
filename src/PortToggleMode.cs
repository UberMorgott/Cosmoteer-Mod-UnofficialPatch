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
/// in an MP profile). The transpiler caches one delegate binding per component; the port also specializes the exact
/// native UIToggle leaf inside a compatibility-validated update. GetToggleMode always reads live part/ship state.
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

    /// <summary>GetToggleMode bound at each site, indexed like <see cref="Targets"/>.</summary>
    internal static readonly MethodInfo[] ToggleModes = Targets.Select(t => Method(t.Target.DeclaringType!, "GetToggleMode")).ToArray();

    private static MethodInfo Method(Type type, string name) =>
        AccessTools.Method(type, name) ?? throw new MissingMethodException(type.Name, name);

    /// <summary>Takes the stack of the replaced <c>newobj Func(object, IntPtr)</c>, with the site index for its pointer.</summary>
    public static Func<ID<PartToggleGuiRules>, int?> Get(object owner, int site)
    {
        if (!Cache.TryGetValue(owner, out Func<ID<PartToggleGuiRules>, int?>? f))
        {
            f = ToggleModes[site].CreateDelegate<Func<ID<PartToggleGuiRules>, int?>>(owner);
            Cache.AddOrUpdate(owner, f);
        }
        return f;
    }

    /// <summary>
    /// Whole vanilla body must match exactly (else throw: Patcher rolls back to vanilla). The port delegates its bool
    /// evaluation to the guarded helper; other sites replace ldftn+newobj with ldc.i4 site+call Get.
    /// Not ldtoken: in a Harmony replacement each ldtoken of a method allocates a RuntimeMethodInfoStub (96 B, more than the Func).
    /// </summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        int site = Array.FindIndex(Targets, t => t.Target.MetadataToken == original.MetadataToken && t.Target.Module == original.Module);
        (MethodInfo target, string sha) = Targets[site];
        List<CodeInstruction> code = Patcher.RequireIl(instructions, sha, $"{target.DeclaringType!.Name}.{target.Name}");
        if (site == 0)
        {
            int evaluationEnd = code.FindIndex(c => c.operand is MethodInfo { Name: "IsBlueprintToggleOn" });
            if (evaluationEnd < 1) throw new InvalidOperationException("Port toggle evaluation site missing");
            // Keep the original SetOperational receiver, call, labels and scheduling. Only its bool argument changes.
            var load = new CodeInstruction(OpCodes.Ldarg_0);
            load.labels.AddRange(code[1].labels);
            code.RemoveRange(1, evaluationEnd);
            code.InsertRange(1, [load, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BlueprintToggleEvaluation), nameof(BlueprintToggleEvaluation.Evaluate)))]);
            return code;
        }
        int i = code.FindIndex(c => c.opcode == OpCodes.Ldftn);
        if (code[i].operand is not MethodInfo m || m.MetadataToken != ToggleModes[site].MetadataToken)
        {
            throw new InvalidOperationException($"{target.DeclaringType.Name}.{target.Name}: ldftn does not bind GetToggleMode");
        }
        code[i].opcode = OpCodes.Ldc_I4;
        code[i].operand = site;
        code[i + 1].opcode = OpCodes.Call;
        code[i + 1].operand = AccessTools.Method(typeof(PortToggleMode), nameof(Get));
        return code;
    }
}
