using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

/// <summary>Patched hot paths must not allocate more than vanilla (regression: per-call ldtoken stub, 96 B per part per frame).</summary>
public class AllocRegressionTests
{
    /// <summary>The transpiled delegate site, run as a DynamicMethod like Harmony's replacement, allocates nothing once cached.</summary>
    [Theory]
    [InlineData(0, "Cosmoteer.Source.Ships.Blueprints.BlueprintPartNetworkPort")]
    [InlineData(1, "Cosmoteer.Ships.Blueprints.Logic.Values.BlueprintPartStatProvider")]
    [InlineData(2, "Cosmoteer.Ships.Blueprints.Logic.BlueprintPartNetworkToggle")]
    public void ToggleSiteAllocatesNothingPerCall(int index, string ownerType)
    {
        MethodInfo target = PortToggleMode.Targets[index].Target;
        List<CodeInstruction> code = PortToggleMode.Transpiler(PatchProcessor.GetOriginalInstructions(target), target).ToList();
        int call = code.FindIndex(c => c.opcode == OpCodes.Call && c.operand is MethodInfo { Name: nameof(PortToggleMode.Get) });
        CodeInstruction site = code[call - 1];

        var dm = new DynamicMethod("site", typeof(object), [typeof(object)], typeof(AllocRegressionTests).Module, skipVisibility: true);
        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(site.opcode, (int)site.operand);
        il.Emit(OpCodes.Call, (MethodInfo)code[call].operand);
        il.Emit(OpCodes.Ret);
        var run = dm.CreateDelegate<Func<object, object>>();
        object owner = RuntimeHelpers.GetUninitializedObject(target.DeclaringType!.Assembly.GetType(ownerType, throwOnError: true)!);

        // Any per-call allocation is >= 24 B a call; below 1 B a call only one-time runtime work fits (a tier-1 promotion
        // landing mid-loop allocates 96 B once, depending on test order; none with DOTNET_TieredCompilation=0).
        Assert.InRange(AllocatedIn(Calls, () => run(owner)), 0, Calls - 1);
    }

    /// <summary>No transpiler adds an allocating instruction (newobj, newarr, box, method/field ldtoken) to a vanilla body.</summary>
    [Fact]
    public void TranspilersAddNoAllocatingInstructions()
    {
        try
        {
            Assert.True(Patcher.Apply());
            Assert.All(Patcher.Patches.Where(p => p.Patch.GetMethod("Transpiler") != null), p =>
            {
                Dictionary<string, int> vanilla = Allocating(PatchProcessor.GetOriginalInstructions(p.Target));
                Assert.All(Allocating(PatchProcessor.GetCurrentInstructions(p.Target)), a =>
                    Assert.True(a.Value <= vanilla.GetValueOrDefault(a.Key), $"{p.Target.DeclaringType?.Name}.{p.Target.Name} adds {a.Key}"));
            });
        }
        finally
        {
            new Harmony(Patcher.HarmonyId).UnpatchAll(Patcher.HarmonyId);
        }
    }

    /// <summary>Count per opcode and allocated type, so a removed allocation cannot hide an added one of another kind.</summary>
    private static Dictionary<string, int> Allocating(List<CodeInstruction> code) => code
        .Where(c => c.opcode == OpCodes.Newobj || c.opcode == OpCodes.Newarr || c.opcode == OpCodes.Box
            || (c.opcode == OpCodes.Ldtoken && c.operand is MethodBase or FieldInfo))
        .GroupBy(c => $"{c.opcode} {(c.operand is ConstructorInfo ctor ? ctor.DeclaringType : c.operand)}")
        .ToDictionary(g => g.Key, g => g.Count());

    private const int Calls = 100_000;

    private static long AllocatedIn(int calls, Action call)
    {
        call(); // JIT and cache fill
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < calls; i++)
        {
            call();
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
