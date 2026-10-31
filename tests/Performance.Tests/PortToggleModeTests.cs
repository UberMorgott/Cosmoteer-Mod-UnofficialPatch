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
[assembly: IgnoresAccessChecksTo("HalflingPlatformWDX")]
// Tests patch the same game methods with Harmony; run them one at a time.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Performance.Tests;

/// <summary>Game assemblies are not copied (the mod compiles against stripped copies); load the real ones from the install.</summary>
internal static class GameAssemblies
{
    public const string GameBin = @"D:\Steam\steamapps\common\Cosmoteer\Bin";

    [ModuleInitializer]
    internal static void Init()
    {
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            string path = Path.Combine(GameBin, name.Name + ".dll");
            return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
        };
        ExcludeTestDynamicAssemblies();
    }

    /// <summary>Dynamic assemblies the tests emit as fixtures (named "Performance.Tests.*").</summary>
    internal static bool IsTestDynamic(Assembly assembly) =>
        assembly.IsDynamic && assembly.GetName().Name is { } name && name.StartsWith("Performance.Tests.", StringComparison.Ordinal);

    /// <summary>
    /// The blueprint toggle skip watches every later assembly load process-wide, and an unknown dynamic assembly fails it for
    /// good; a fixture emitted by any test would otherwise disable it for every later test. Only the tests' scan hook changes:
    /// <see cref="BlueprintToggleUpdates.ScanAssembly"/> (production) still refuses such assemblies.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExcludeTestDynamicAssemblies()
    {
        Func<Assembly, string?> scan = BlueprintToggleUpdates.Scan;
        BlueprintToggleUpdates.Scan = assembly => IsTestDynamic(assembly) ? null : scan(assembly);
    }
}

public class PortToggleModeTests
{
    public static TheoryData<int> TargetIndices => new(Enumerable.Range(0, PortToggleMode.Targets.Length));

    private static MethodInfo Target(int i) => PortToggleMode.Targets[i].Target;

    [Theory]
    [MemberData(nameof(TargetIndices))]
    public void TranspilerMatchesRealGameIl(int index)
    {
        var original = PatchProcessor.GetOriginalInstructions(Target(index));
        var patched = PortToggleMode.Transpiler(original, Target(index)).ToList();

        Assert.DoesNotContain(patched, i => i.opcode == OpCodes.Ldftn || i.opcode == OpCodes.Newobj);
        Assert.DoesNotContain(patched, i => i.opcode == OpCodes.Ldtoken);
        if (index == 0)
        {
            Assert.Single(patched, i => i.operand is MethodInfo { Name: nameof(BlueprintToggleEvaluation.Evaluate) } m && m.DeclaringType == typeof(BlueprintToggleEvaluation));
            Assert.DoesNotContain(patched, i => i.operand is MethodInfo { Name: "IsBlueprintToggleOn" });
            Assert.Single(patched, i => i.operand is MethodInfo { Name: "SetOperational" });
            return;
        }
        Assert.Equal(original.Count, patched.Count);
        Assert.Single(patched, i => i.opcode == OpCodes.Ldc_I4 && i.operand is int site && site == index);
        Assert.Equal("GetToggleMode", PortToggleMode.ToggleModes[index].Name);
        Assert.Equal(Target(index).DeclaringType, PortToggleMode.ToggleModes[index].DeclaringType);
        Assert.Single(patched, i => i.opcode == OpCodes.Call && i.operand is MethodInfo { Name: nameof(PortToggleMode.Get) } m && m.DeclaringType == typeof(PortToggleMode));
    }

    [Theory]
    [MemberData(nameof(TargetIndices))]
    public void TranspilerRejectsChangedIl(int index)
    {
        var original = PatchProcessor.GetOriginalInstructions(Target(index));
        original.Insert(0, new CodeInstruction(OpCodes.Nop));

        Assert.Throws<InvalidOperationException>(() => PortToggleMode.Transpiler(original, Target(index)).ToList());
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
            harmony.Patch(Target(0), transpiler: rejit);
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

    /// <summary>Concrete owner of each target (the network port target is declared on an abstract base).</summary>
    [Theory]
    [InlineData(0, "Cosmoteer.Source.Ships.Blueprints.BlueprintPartNetworkPort")]
    [InlineData(1, "Cosmoteer.Ships.Blueprints.Logic.Values.BlueprintPartStatProvider")]
    [InlineData(2, "Cosmoteer.Ships.Blueprints.Logic.BlueprintPartNetworkToggle")]
    public void CacheBindsOneDelegatePerOwner(int index, string ownerType)
    {
        Type owner = Target(index).DeclaringType!.Assembly.GetType(ownerType, throwOnError: true)!;
        object a = RuntimeHelpers.GetUninitializedObject(owner);
        object b = RuntimeHelpers.GetUninitializedObject(owner);

        var a1 = PortToggleMode.Get(a, index);
        var a2 = PortToggleMode.Get(a, index);
        var b1 = PortToggleMode.Get(b, index);

        Assert.Same(a1, a2);
        Assert.NotSame(a1, b1);
        Assert.Same(a, a1.Target);
        Assert.Same(b, b1.Target);
        Assert.Equal(AccessTools.Method(Target(index).DeclaringType, "GetToggleMode"), a1.Method);
    }
}
