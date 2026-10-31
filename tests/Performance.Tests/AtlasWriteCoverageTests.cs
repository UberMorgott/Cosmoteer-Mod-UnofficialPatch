using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships.Rendering;
using Halfling.Geometry;
using Halfling.Graphics;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

public sealed class AtlasWriteCoverageTests
{
    private const string HarmonyId = "performance.tests.atlas-write-coverage";
    private static readonly Dictionary<MethodBase, int> Calls = new();

    [Theory]
    [InlineData(0, 0x06006276)]
    [InlineData(1, 0x0600171D)]
    [InlineData(2, 0x06006278)]
    [InlineData(3, 0x06006279)]
    [InlineData(4, 0x0600171F)]
    [InlineData(5, 0x0600171C)]
    public void WriterPinsMatchInstalledMethodsAndRejectChanges(int index, int token)
    {
        MethodInfo method = AtlasWriteCoverage.Targets[index].Target;
        Assert.Equal(token, method.MetadataToken);
        Assert.Equal(typeof(AtlasQuadManager).Module, method.Module);
        Assert.NotEqual(Guid.Empty, method.Module.ModuleVersionId);
        Assert.False(method.ContainsGenericParameters);
        Assert.Empty(Patcher.FindR2RInliners(method));
        var code = PatchProcessor.GetOriginalInstructions(method);
        Assert.Equal(code.Count, AtlasWriteCoverage.RequireIl(method, code).Count);
        code.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => AtlasWriteCoverage.RequireIl(method, code));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BothNativeDrawScopesArePinnedAndHaveNoPrecompiledInliners(int index)
    {
        MethodInfo method = AtlasWriteCoverage.DrawTargets[index].Target;
        Assert.False(method.ContainsGenericParameters);
        Assert.Empty(Patcher.FindR2RInliners(method));
        var code = PatchProcessor.GetOriginalInstructions(method);
        Assert.Equal(code.Count, AtlasWriteCoverage.RequireIl(method, code).Count);
        code.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => AtlasWriteCoverage.RequireIl(method, code));
    }

    [Fact]
    public void DisableInliningFailsClosedWhenApiMissingOrRefuses()
    {
        Assert.False(AtlasWriteCoverage.TryDisableInlining(null, AtlasWriteCoverage.DataSetter, out string? missing));
        Assert.NotNull(missing);
        Assert.False(RefusingPlatform.Current.TryDisableInlining(AtlasWriteCoverage.DataSetter));
        Assert.False(AtlasWriteCoverage.TryDisableInlining(typeof(RefusingPlatform), AtlasWriteCoverage.DataSetter, out string? refused));
        Assert.NotNull(refused);
    }

    private sealed class RefusingPlatform
    {
        public static RefusingPlatform Current { get; } = new();
        public bool TryDisableInlining(MethodBase _) => false;
    }

    [Fact]
    public void StartupRejectsLateFramesAndExistingAtlasArrays()
    {
        Assert.False(AtlasWriteCoverage.TryValidateStartup(1, Array.Empty<WeakReference<IDisposable>>(), out _));
        Assert.True(AtlasWriteCoverage.TryValidateStartup(0, Array.Empty<WeakReference<IDisposable>>(), out string? emptyError), emptyError);
        using var array = new FakeGraphicsArray();
        WeakReference<IDisposable>[] owned = [new(array)];
        Assert.False(AtlasWriteCoverage.TryValidateStartup(0, owned, out string? existing));
        Assert.NotNull(existing);
        Assert.False(AtlasWriteCoverage.TryValidateStartup(0, new object(), out _));
        GC.KeepAlive(array);
    }

    [Fact]
    public void FutureOptimizedExactNativeCallerCannotBypassSetterHook()
    {
        using var fixture = new Fixture();
        fixture.Manager.CreateQuad(default, 0);
        AtlasQuad value = default;
        value.V1.Location = new Vector3(17, 3, 1);
        Calls.Clear();
        MakeOptimizedExactWriter()(fixture.Manager, value, 10);
        Assert.Empty(Calls);
        Assert.True(AtlasWriteCoverage.TryPrepare(out string? error), error);
        var harmony = new Harmony(HarmonyId);
        try
        {
            Patch(harmony, AtlasWriteCoverage.DataSetter);
            // A new method is emitted and JIT-compiled only after patching; newobj exposes the exact private native type.
            Action<AtlasQuadManager, AtlasQuad, int> write = MakeOptimizedExactWriter();
            write(fixture.Manager, value, 50_000);
            Assert.Equal(50_000, Calls[AtlasWriteCoverage.DataSetter]);
            Assert.Equal(50_011U, fixture.Manager.ChangeCount);
            Assert.Equal(value.V1.Location, fixture.Array[0].V1.Location);
        }
        finally
        {
            harmony.UnpatchAll(HarmonyId);
        }
    }

    [Fact]
    public void NativeCreateMoveRemoveClearAndDisposeStayObservedAndDataGetterReturnsCopy()
    {
        Assert.True(AtlasWriteCoverage.TryPrepare(out string? error), error);
        var harmony = new Harmony(HarmonyId);
        using var fixture = new Fixture();
        Calls.Clear();
        try
        {
            foreach ((MethodInfo method, _) in AtlasWriteCoverage.Targets) Patch(harmony, method);
            ManagedAtlasQuad first = fixture.Manager.CreateQuad(default, 0);
            fixture.Manager.CreateQuad(default, 0);
            AtlasQuad value = default;
            value.V1.Location = new Vector3(23, 4, 2);
            ManagedAtlasQuad last = fixture.Manager.CreateQuad(value, 0);
            AtlasQuad copy = last.Data;
            copy.V1.Location = new Vector3(999, 0, 0);
            Assert.Equal(999, copy.V1.Location.X);
            Assert.Equal(value.V1.Location, last.Data.V1.Location);
            Assert.Equal(typeof(AtlasQuad), AccessTools.PropertyGetter(AtlasWriteCoverage.InternalQuadType, "Data").ReturnType);
            first.Data = value;
            first.Dispose(); // Non-last removal must call native MoveIndex and copy the last entry into index 0.
            Assert.Equal(value.V1.Location, fixture.Array[0].V1.Location);
            Assert.Equal(2, fixture.Manager.Count);
            fixture.Manager.Clear();
            Assert.Equal(0, fixture.Manager.Count);
            fixture.Manager.Dispose();
            Assert.True(fixture.Array.WasDisposed);
            Assert.Equal(3, Calls[AtlasWriteCoverage.Targets[1].Target]);
            Assert.Equal(3, Calls[AtlasWriteCoverage.Targets[2].Target]);
            Assert.Equal(1, Calls[AtlasWriteCoverage.Targets[3].Target]);
            Assert.Equal(1, Calls[AtlasWriteCoverage.Targets[4].Target]);
            Assert.Equal(1, Calls[AtlasWriteCoverage.Targets[5].Target]);
        }
        finally
        {
            harmony.UnpatchAll(HarmonyId);
        }
    }

    private static void Patch(Harmony harmony, MethodInfo method) => harmony.Patch(method,
        prefix: new HarmonyMethod(typeof(AtlasWriteCoverageTests), nameof(Observed)),
        transpiler: new HarmonyMethod(typeof(AtlasWriteCoverageTests), nameof(VerifyBody)));

    private static void Observed(MethodBase __originalMethod)
    {
        Calls.TryGetValue(__originalMethod, out int previous);
        Calls[__originalMethod] = previous + 1;
    }

    private static IEnumerable<CodeInstruction> VerifyBody(IEnumerable<CodeInstruction> instructions, MethodBase original) => AtlasWriteCoverage.RequireIl(original, instructions);

    private static Action<AtlasQuadManager, AtlasQuad, int> MakeOptimizedExactWriter()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Performance.Tests.AtlasExactWriter" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(IgnoresAccessChecksToAttribute).GetConstructor([typeof(string)])!, ["Cosmoteer"]));
        TypeBuilder type = assembly.DefineDynamicModule("writer").DefineType("Writer", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder method = type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [typeof(AtlasQuadManager), typeof(AtlasQuad), typeof(int)]);
        // CLI implementation flags form a bitmask even though MethodImplAttributes lacks [Flags].
        // ReSharper disable once BitwiseOperatorOnEnumWithoutFlags
        method.SetImplementationFlags(MethodImplAttributes.NoInlining | MethodImplAttributes.AggressiveOptimization);
        ILGenerator il = method.GetILGenerator();
        LocalBuilder quad = il.DeclareLocal(AtlasWriteCoverage.InternalQuadType);
        LocalBuilder index = il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newobj, AccessTools.Constructor(AtlasWriteCoverage.InternalQuadType, [typeof(AtlasQuadManager), typeof(int), typeof(int)]));
        il.Emit(OpCodes.Stloc, quad);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, index);
        Label check = il.DefineLabel(), body = il.DefineLabel();
        il.Emit(OpCodes.Br, check);
        il.MarkLabel(body);
        il.Emit(OpCodes.Ldloc, quad);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, AtlasWriteCoverage.DataSetter);
        il.Emit(OpCodes.Ldloc, index);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, index);
        il.MarkLabel(check);
        il.Emit(OpCodes.Ldloc, index);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Blt, body);
        il.Emit(OpCodes.Ret);
        return type.CreateType().GetMethod("Run")!.CreateDelegate<Action<AtlasQuadManager, AtlasQuad, int>>();
    }

    private sealed class Fixture : IDisposable
    {
        public readonly AtlasQuadManager Manager = (AtlasQuadManager)RuntimeHelpers.GetUninitializedObject(typeof(AtlasQuadManager));
        public readonly FakeGraphicsArray Array = new();

        public Fixture()
        {
            var list = (GraphicsList<AtlasQuad>)RuntimeHelpers.GetUninitializedObject(typeof(GraphicsList<AtlasQuad>));
            AccessTools.Field(typeof(GraphicsList<AtlasQuad>), "_array").SetValue(list, Array);
            Manager._lists = [list];
            Manager._managedAtlasQuads = [new()];
        }

        public void Dispose() => Manager.Dispose();
    }

    private sealed class FakeGraphicsArray() : GraphicsArray<AtlasQuad>(PerformanceMode.Dynamic)
    {
        private AtlasQuad[] _values = GC.AllocateArray<AtlasQuad>(64, pinned: true);
        public bool WasDisposed { get; private set; }
        public override AtlasQuad this[int i] { get => _values[i]; set => _values[i] = value; }
        public override int Size => _values.Length;
        public override string? DebugName { get; set; }
        public override ref AtlasQuad GetRef(int index, bool readOnly = false) => ref _values[index];
        public override void Resize(int size, bool keepData = true)
        {
            AtlasQuad[] values = GC.AllocateArray<AtlasQuad>(size, pinned: true);
            if (keepData) Array.Copy(_values, values, Math.Min(size, _values.Length));
            _values = values;
        }
        public override unsafe AtlasQuad* GetUnsafeTempPointer(bool readOnly)
        {
            fixed (AtlasQuad* pointer = _values) return pointer;
        }
        public override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
