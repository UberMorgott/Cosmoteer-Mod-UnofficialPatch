using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Buffs;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Statuses;
using Cosmoteer.Ships.Statuses.Subhandlers;
using Halfling;
using Halfling.Geometry;
using Halfling.Pooling;
using Halfling.Timing;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

[Collection(nameof(StatusTempDisposeTests))]
public sealed class StatusTempDisposeTests
{
    private const string TestId = "morgott.performance.tests.statusdispose";

    [Fact]
    public void KillSwitchIsOnlyZero()
    {
        Assert.False(StatusTempDispose.IsRequested("0"));
        Assert.True(StatusTempDispose.IsRequested(null));
        Assert.True(StatusTempDispose.IsRequested("1"));
    }

    [Fact]
    public void BothGameInstantiationsArePatched()
    {
        Type[] handlers = typeof(StatusHandler<>).Assembly.GetTypes()
            .Select(t => t.BaseType)
            .Where(b => b is { IsGenericType: true } && b.GetGenericTypeDefinition() == typeof(StatusHandler<>))
            .Distinct().OrderBy(t => t!.FullName).ToArray()!;
        Assert.Equal(StatusTempDispose.Targets.Select(t => t.Target.DeclaringType).OrderBy(t => t!.FullName), handlers);
    }

    [Fact]
    public void TranspilerMatchesRealGameIl()
    {
        foreach ((MethodInfo target, _) in StatusTempDispose.Targets)
        {
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(target);
            List<CodeInstruction> code = StatusTempDispose.Transpiler(original, target, new DynamicMethod("x", null, null).GetILGenerator()).ToList();
            Assert.Equal(original.Count + 8, code.Count);
            Assert.Equal(OpCodes.Leave, code[original.Count - 1].opcode);
            Assert.Equal(ExceptionBlockType.BeginExceptionBlock, Assert.Single(code[7].blocks).blockType);
            Assert.Equal(ExceptionBlockType.BeginExceptionBlock, Assert.Single(code[9].blocks).blockType);
            Assert.True(code[^7].Calls(AccessTools.Method(typeof(TempDictionary<StatusType, IStatusLocationInfo>), "Dispose")));
            Assert.Equal("Dispose", ((MethodInfo)code[^3].operand).Name);
            Assert.Equal(OpCodes.Ret, code[^1].opcode);
        }
    }

    [Fact]
    public void TranspilerRejectsChangedIl()
    {
        foreach ((MethodInfo target, _) in StatusTempDispose.Targets)
        {
            List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(target);
            code.Insert(0, new CodeInstruction(OpCodes.Nop));
            Assert.Throws<InvalidOperationException>(() => StatusTempDispose.Transpiler(code, target, new DynamicMethod("x", null, null).GetILGenerator()).ToList());
        }
    }

    /// <summary>Same modulator inputs, value writes and deferred notifications as vanilla; then dictionary, then list go back to the pool.</summary>
    [Fact]
    public void TileHandlerKeepsVanillaOrderAndRecycles() =>
        KeepsVanillaOrderAndRecycles<IntVector2, TileStatusHandler, TileStatusEffectDataProvider>(i => new IntVector2(i * 3 - 7, 11 - i), l => $"{l.X},{l.Y}");

    [Fact]
    public void PartHandlerKeepsVanillaOrderAndRecycles()
    {
        var parts = new Dictionary<Part, int>();
        KeepsVanillaOrderAndRecycles<Part, PartStatusHandler, PartStatusEffectDataProvider>(i =>
        {
            var part = (Part)RuntimeHelpers.GetUninitializedObject(typeof(Part));
            parts[part] = i;
            return part;
        }, p => "part" + parts[p]);
    }

    [Fact]
    public void TileHandlerRecyclesWhenModulatorThrows() =>
        RecyclesOnThrow<IntVector2, TileStatusHandler, TileStatusEffectDataProvider>(i => new IntVector2(i, -i), l => $"{l.X},{l.Y}");

    private static void KeepsVanillaOrderAndRecycles<T, THandler, TProvider>(Func<int, T> location, Func<T, string> name) where T : notnull
    {
        Harmony stubs = Stubs<T, TProvider>(name);
        var patch = new Harmony(TestId);
        try
        {
            (object handler, object closure, Status<T>[] statuses) = Handler<T, THandler, TProvider>(location);
            MethodInfo target = StatusTempDispose.Targets.Single(t => t.Target.DeclaringType == typeof(StatusHandler<T>)).Target;

            // Reference: the unchanged vanilla IL, JIT-compiled (the precompiled body may inline stubbed callees).
            patch.Patch(target, transpiler: new HarmonyMethod(typeof(Patcher), nameof(Patcher.Rejit)));
            string vanilla = Run(target, handler, closure, statuses, out Dictionary<StatusType, IStatusLocationInfo> vanillaDict);
            Assert.NotEmpty(vanillaDict); // vanilla leaks the dictionary with the last location's statuses
            Assert.Contains("resist", vanilla);
            Assert.Contains("notify", vanilla);
            patch.UnpatchAll(TestId);
            patch.Patch(target, transpiler: new HarmonyMethod(typeof(StatusTempDispose), nameof(StatusTempDispose.Transpiler)));
            string patched = Run(target, handler, closure, statuses, out Dictionary<StatusType, IStatusLocationInfo> patchedDict);

            Assert.Equal(vanilla.Replace("\nfinal ", "\ndispose dict 1\ndispose list 3\nfinal "), patched);
            Assert.Empty(patchedDict); // recycled (pool deinitializer clears it)
        }
        finally
        {
            patch.UnpatchAll(TestId);
            stubs.UnpatchAll(TestId + ".stubs");
        }
    }

    private static void RecyclesOnThrow<T, THandler, TProvider>(Func<int, T> location, Func<T, string> name) where T : notnull
    {
        Harmony stubs = Stubs<T, TProvider>(name);
        var patch = new Harmony(TestId);
        try
        {
            (object handler, object closure, Status<T>[] statuses) = Handler<T, THandler, TProvider>(location);
            MethodInfo target = StatusTempDispose.Targets.Single(t => t.Target.DeclaringType == typeof(StatusHandler<T>)).Target;
            patch.Patch(target, transpiler: new HarmonyMethod(typeof(StatusTempDispose), nameof(StatusTempDispose.Transpiler)));

            _throwAtModulate = 2;
            string modulate = Run(target, handler, closure, statuses, out _, expectThrow: true);
            Assert.EndsWith("modulate throws\ndispose dict 1\ndispose list 1\nfinal ", modulate[..(modulate.LastIndexOf("final ", StringComparison.Ordinal) + 6)]);

            _throwAtModulate = -1;
            _throwAtNotify = true;
            string notify = Run(target, handler, closure, statuses, out _, expectThrow: true);
            Assert.EndsWith("notify throws\ndispose dict 1\ndispose list 3\nfinal ", notify[..(notify.LastIndexOf("final ", StringComparison.Ordinal) + 6)]);
        }
        finally
        {
            _throwAtModulate = -1;
            _throwAtNotify = false;
            patch.UnpatchAll(TestId);
            stubs.UnpatchAll(TestId + ".stubs");
        }
    }

    [ThreadStatic] private static List<string>? _calls;
    [ThreadStatic] private static Dictionary<StatusType, IStatusLocationInfo>? _dict;
    [ThreadStatic] private static Func<object, string>? _describe;
    [ThreadStatic] private static StatusType? _type;
    [ThreadStatic] private static int _modulateCount;
    private static int _throwAtModulate = -1;
    private static bool _throwAtNotify;

    private static string Run<T>(MethodInfo target, object handler, object closure, Status<T>[] statuses, out Dictionary<StatusType, IStatusLocationInfo> dict, bool expectThrow = false)
        where T : notnull
    {
        float[] start = [2.5f, 6f, -1.25f, 7f];
        for (int i = 0; i < statuses.Length; i++) statuses[i].Value = start[i];
        _calls = [];
        _dict = null;
        _modulateCount = 0;
        try
        {
            if (expectThrow)
                Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => target.Invoke(handler, [closure])).InnerException);
            else
                target.Invoke(handler, [closure]);
            dict = _dict!;
            _calls.Add("final " + string.Join(";", statuses.Select(s => _describe!(s))));
            return string.Join("\n", _calls);
        }
        finally
        {
            _calls = null;
        }
    }

    private static Harmony Stubs<T, TProvider>(Func<T, string> name) where T : notnull
    {
        _describe = o => name(((Status<T>)o).Location) + "=" + Bits(((Status<T>)o).Value);
        var stubs = new Harmony(TestId + ".stubs");
        stubs.Patch(AccessTools.Method(typeof(TProvider), "GetBuffs"), prefix: Stub(nameof(BuffsStub)));
        stubs.Patch(AccessTools.Method(typeof(TProvider), "PopulateStatuses", [typeof(Status<T>), typeof(Ship), typeof(Dictionary<StatusType, IStatusLocationInfo>)]), prefix: Stub(nameof(PopulateStub)));
        stubs.Patch(AccessTools.Method(typeof(StatusHandler<T>), "<Halfling.Scene2D.IFixedUpdateableSceneObject.FixedUpdate>g___GetValueResist|65_1"), prefix: Stub(nameof(ResistStub)));
        stubs.Patch(AccessTools.Method(typeof(MultiValueModulatorRules), nameof(MultiValueModulatorRules.ModulateValue)), prefix: Stub(nameof(ModulateStub)));
        stubs.Patch(AccessTools.Method(typeof(StatusHandler<T>), "OnStatusValueModified", [typeof(Status<T>), typeof(float), typeof(StatusList<T>), typeof(bool)]), prefix: Stub(nameof(NotifyStub)));
        MethodInfo listDispose = AccessTools.Method(typeof(TempList<(Status<T>, float)>), "Dispose");
        MethodInfo dictDispose = AccessTools.Method(typeof(TempDictionary<StatusType, IStatusLocationInfo>), "Dispose");
        Assert.True(Patcher.TryDisableInlining(listDispose, out string? e1), e1);
        Assert.True(Patcher.TryDisableInlining(dictDispose, out string? e2), e2);
        stubs.Patch(listDispose, prefix: Stub(nameof(ListDisposeStub)));
        stubs.Patch(dictDispose, prefix: Stub(nameof(DictDisposeStub)));
        return stubs;
    }

    private static HarmonyMethod Stub(string name) => new(typeof(StatusTempDisposeTests), name);

    private static (object Handler, object Closure, Status<T>[] Statuses) Handler<T, THandler, TProvider>(Func<int, T> location) where T : notnull
    {
        var type = (StatusType)RuntimeHelpers.GetUninitializedObject(typeof(StatusType));
        type.ValueModulators = (MultiValueModulatorRules)RuntimeHelpers.GetUninitializedObject(typeof(MultiValueModulatorRules));
        type.ValueClampRange = new Range<float>(-4f, 6f);
        _type = type;
        var store = new StatusStore<T>(type);
        var statuses = new Status<T>[4];
        for (int i = 0; i < statuses.Length; i++)
        {
            statuses[i] = new Status<T>(location(i % 3), default, default, 0f);
            store.GetOrCreateStatusList(statuses[i].Location).Add(statuses[i], silent: true);
        }
        Status<T>[] order = store.ToArray();
        var applicator = RuntimeHelpers.GetUninitializedObject(typeof(StatusEffectApplicator<T>));
        AccessTools.Field(typeof(StatusEffectApplicator<T>), "<EffectDataProvider>k__BackingField").SetValue(applicator, RuntimeHelpers.GetUninitializedObject(typeof(TProvider)));
        object handler = RuntimeHelpers.GetUninitializedObject(typeof(THandler));
        AccessTools.Field(typeof(StatusHandler<T>), "<StatusType>k__BackingField").SetValue(handler, type);
        AccessTools.Field(typeof(StatusHandler<T>), "<Store>k__BackingField").SetValue(handler, store);
        AccessTools.Field(typeof(StatusHandler<T>), "_effectApplicator").SetValue(handler, applicator);
        AccessTools.Field(typeof(StatusHandler<T>), "Ship").SetValue(handler, RuntimeHelpers.GetUninitializedObject(typeof(Ship)));
        Type closureType = StatusTempDispose.Targets.Single(t => t.Target.DeclaringType == typeof(StatusHandler<T>)).Target.GetParameters()[0].ParameterType.GetElementType()!;
        object closure = Activator.CreateInstance(closureType)!;
        AccessTools.Field(closureType, "fixedUpdater").SetValue(closure, RuntimeHelpers.GetUninitializedObject(typeof(FixedUpdater)));
        return (handler, closure, order);
    }

    private static string Bits(float f) => BitConverter.SingleToInt32Bits(f).ToString("X8");

    public static bool BuffsStub(ref IReadOnlyDictionary<BuffType, float>? __result)
    {
        __result = null;
        return false;
    }

    public static bool PopulateStub(object[] __args)
    {
        var dict = (Dictionary<StatusType, IStatusLocationInfo>)__args[2];
        _calls?.Add($"populate {_describe!(__args[0])} count={dict.Count} same={ReferenceEquals(dict, _dict ?? dict)}");
        _dict = dict;
        dict.Add(_type!, null!);
        return false;
    }

    public static bool ResistStub(object[] __args, ref float __result)
    {
        __result = 0.125f * (__args[0].GetHashCode() % 5);
        if (__args[0] is IntVector2 v) __result = 0.125f * (v.X - v.Y);
        _calls?.Add("resist " + Bits(__result));
        return false;
    }

    public static bool ModulateStub(ValueModulationData data, ref float __result)
    {
        if (_modulateCount++ == _throwAtModulate)
        {
            _calls?.Add("modulate throws");
            throw new InvalidOperationException("modulate");
        }
        __result = BitConverter.SingleToInt32Bits(data.Value) == BitConverter.SingleToInt32Bits(6f) ? 100f : data.Value * 0.75f + data.ValueResistance - 9f / 7f; // 6 clamps back to 6: unchanged, not notified
        _calls?.Add($"modulate {Bits(data.Value)} {Bits(data.ValueResistance)} statuses={ReferenceEquals(data.Statuses, _dict)} → {Bits(__result)}");
        return false;
    }

    public static bool NotifyStub(object[] __args)
    {
        _calls?.Add($"notify {_describe!(__args[0])} old={Bits((float)__args[1])} silent={__args[3]}");
        if (_throwAtNotify)
        {
            _calls?.Add("notify throws");
            throw new InvalidOperationException("notify");
        }
        return false;
    }

    public static bool ListDisposeStub(object __instance)
    {
        _calls?.Add("dispose list " + ((System.Collections.ICollection)__instance).Count);
        return true;
    }

    public static bool DictDisposeStub(Dictionary<StatusType, IStatusLocationInfo> __instance)
    {
        _calls?.Add("dispose dict " + __instance.Count);
        return true;
    }
}
