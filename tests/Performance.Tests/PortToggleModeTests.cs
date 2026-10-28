using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using Xunit;

// Same platform as the mod under test (the game is Windows-only).
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
// Typed access to internal game members, as in the mod (attribute type comes from the mod via InternalsVisibleTo).
[assembly: IgnoresAccessChecksTo("Cosmoteer")]
[assembly: IgnoresAccessChecksTo("HalflingCore")]
// Tests patch the same game methods with Harmony; run them one at a time.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Performance.Tests;

/// <summary>Game assemblies are not copied (the mod compiles against stripped copies); load the real ones from the install.</summary>
internal static class GameAssemblies
{
    public const string GameBin = @"D:\Steam\steamapps\common\Cosmoteer\Bin";

    [ModuleInitializer]
    internal static void Init() =>
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            string path = Path.Combine(GameBin, name.Name + ".dll");
            return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
        };
}

public class PortToggleModeTests
{
    private static readonly MethodInfo Get = typeof(PortToggleMode).GetMethod(nameof(PortToggleMode.Get))!;

    [Fact]
    public void TranspilerMatchesRealGameIl()
    {
        var original = PatchProcessor.GetOriginalInstructions(Patcher.Target);
        var patched = PortToggleMode.Transpiler(original).ToList();

        Assert.Equal(original.Count - 1, patched.Count);
        Assert.DoesNotContain(patched, i => i.opcode == OpCodes.Ldftn || i.opcode == OpCodes.Newobj);
        Assert.Single(patched, i => i.opcode == OpCodes.Call && Equals(i.operand, Get));
    }

    [Fact]
    public void TranspilerRejectsChangedIl()
    {
        var original = PatchProcessor.GetOriginalInstructions(Patcher.Target);
        original.Insert(0, new CodeInstruction(OpCodes.Nop));

        Assert.Throws<InvalidOperationException>(() => PortToggleMode.Transpiler(original).ToList());
    }

    [Fact]
    public void ApplyPatchesRealMethod()
    {
        try
        {
            Log.Init(); // Performance.log next to the test DLL lists the patched R2R inliners
            Assert.True(Patcher.Apply());
            foreach ((MethodBase target, _) in Patcher.Patches)
            {
                Assert.True(Harmony.GetPatchInfo(target)!.Owners.Contains(Patcher.HarmonyId), target.Name);
                Assert.All(Patcher.FindR2RInliners(target), m => Assert.Single(Harmony.GetPatchInfo(m)!.Transpilers));
            }
        }
        finally
        {
            new Harmony(Patcher.HarmonyId).UnpatchAll(Patcher.HarmonyId);
        }
    }

    [Fact]
    public void RollBackConfirmsVanillaOrThrowsOnResidue()
    {
        var harmony = new Harmony(Patcher.HarmonyId);
        var rejit = new HarmonyMethod(typeof(Patcher).GetMethod(nameof(Patcher.Rejit)));
        try
        {
            harmony.Patch(Patcher.Target, transpiler: rejit);
            Assert.Throws<InvalidOperationException>(() => Patcher.RollBack(() => throw new InvalidOperationException("injected")));
            Assert.True(Harmony.HasAnyPatches(Patcher.HarmonyId));

            Patcher.RollBack(() => harmony.UnpatchAll(Patcher.HarmonyId));
            Assert.False(Harmony.HasAnyPatches(Patcher.HarmonyId));
        }
        finally
        {
            harmony.UnpatchAll(Patcher.HarmonyId);
        }
    }

    [Fact]
    public void CacheBindsOneDelegatePerPort()
    {
        Type port = Patcher.Target.DeclaringType!.Assembly.GetType("Cosmoteer.Source.Ships.Blueprints.BlueprintPartNetworkPort", throwOnError: true)!;
        object a = RuntimeHelpers.GetUninitializedObject(port);
        object b = RuntimeHelpers.GetUninitializedObject(port);

        var a1 = (Delegate)Get.Invoke(null, new[] { a })!;
        var a2 = (Delegate)Get.Invoke(null, new[] { a })!;
        var b1 = (Delegate)Get.Invoke(null, new[] { b })!;

        Assert.Same(a1, a2);
        Assert.NotSame(a1, b1);
        Assert.Same(a, a1.Target);
        Assert.Same(b, b1.Target);
        Assert.Equal("GetToggleMode", a1.Method.Name);
    }
}
