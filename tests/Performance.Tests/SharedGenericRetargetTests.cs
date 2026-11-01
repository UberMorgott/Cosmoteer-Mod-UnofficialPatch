using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Cosmoteer.Ships.Blueprints;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Statuses;
using Halfling.Geometry;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

/// <summary>Runs alone: toggles the process-wide retarget listener and depends on tiered compilation timing.</summary>
[CollectionDefinition(nameof(SharedGenericRetargetTests), DisableParallelization = true)]
public sealed class SharedGenericRetargetSerial;

[Collection(nameof(SharedGenericRetargetTests))]
public sealed class SharedGenericRetargetTests
{
    private const string TestId = "morgott.performance.tests.retarget";
    private const int Rounds = 6, CallsPerRound = 500, PauseMs = 400;
    private static int _hits, _forwarded;
    private static readonly bool Tiered = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") != "0";

    [Fact]
    public void ClassifiesSharedGenericTargets()
    {
        Assert.True(SharedGenericRetarget.IsShared(BoundingPoints.Target));
        Assert.True(SharedGenericRetarget.IsShared(StatusTempDispose.Targets.Single(t => t.Target.DeclaringType == typeof(StatusHandler<Part>)).Target));
        Assert.False(SharedGenericRetarget.IsShared(StatusTempDispose.Targets.Single(t => t.Target.DeclaringType == typeof(StatusHandler<IntVector2>)).Target));
        Assert.False(SharedGenericRetarget.IsShared(PlayerMoney.Target));
        Assert.True(SharedGenericRetarget.IsShared(AccessTools.Method(typeof(List<KeyValuePair<string, int>>), nameof(List<int>.Clear))));
        Assert.False(SharedGenericRetarget.IsShared(AccessTools.Method(typeof(List<KeyValuePair<int, int>>), nameof(List<int>.Clear))));
    }

    [Fact]
    public void SharesCodeMatchesOnlyTheCanonicalForm()
    {
        Type canon = typeof(object).Assembly.GetType("System.__Canon")!;
        MethodBase canonical = MethodOf(typeof(CommonBasePartsManager<>).MakeGenericType(canon), "GetBoundingPoints");
        Assert.True(SharedGenericRetarget.SharesCode(BoundingPoints.Target, canonical));
        Assert.True(SharedGenericRetarget.SharesCode(MethodOf(typeof(CommonBasePartsManager<BlueprintPart>), "GetBoundingPoints"), canonical));
        Assert.False(SharedGenericRetarget.SharesCode(MethodOf(typeof(CommonBasePartsManager<Part>), "AddArbitraryMass"), canonical));
        MethodBase canonicalStatus = MethodOf(typeof(StatusHandler<>).MakeGenericType(canon), StatusTempDispose.Targets[0].Target.Name);
        Assert.True(SharedGenericRetarget.SharesCode(StatusTempDispose.Targets[1].Target, canonicalStatus));
        Assert.False(SharedGenericRetarget.SharesCode(StatusTempDispose.Targets[0].Target, canonicalStatus)); // IntVector2: own code
    }

    /// <summary>
    /// The MonoMod bug this listener repairs: without it a patch on shared generic code stops intercepting once the runtime
    /// recompiles the method at a higher tier. Documents that the guards below really exercise a tier-up. Probes are unused game
    /// methods, one per test (a method tiers up once): the game DLL is optimized (R2R, tiers up), a Debug test assembly would not.
    /// </summary>
    [Fact]
    public void WithoutListenerSharedGenericPatchIsLostAfterTierUp()
    {
        Assert.SkipUnless(Tiered, "tiered compilation disabled");
        bool wasEnabled = Reset();
        try
        {
            int[] hits = Intercepts(MethodOf(typeof(CommonBasePartsManager<Part>), "AddArbitraryMass"));
            Assert.Equal(CallsPerRound, hits[0]);
            Assert.Contains(hits, h => h < CallsPerRound);
        }
        finally
        {
            Restore(wasEnabled);
        }
    }

    [Fact]
    public void ListenerKeepsRegisteredSharedGenericPatchThroughTierUp()
    {
        Assert.SkipUnless(Tiered, "tiered compilation disabled");
        bool wasEnabled = Reset();
        try
        {
            MethodBase probe = MethodOf(typeof(CommonBasePartsManager<Part>), "HasPart");
            Assert.True(SharedGenericRetarget.Enable([probe], out string? error), error);
            int before = SharedGenericRetarget.Retargets;
            int[] hits = Intercepts(probe);
            Assert.All(hits, h => Assert.Equal(CallsPerRound, h));
            Assert.True(SharedGenericRetarget.Retargets > before, "no tier-up was retargeted");
            Assert.False(SharedGenericRetarget.Failed);
        }
        finally
        {
            Restore(wasEnabled);
        }
    }

    /// <summary>A shared generic detour that is not a registered family (another mod's) is never forwarded.</summary>
    [Fact]
    public void ListenerLeavesForeignSharedGenericDetoursAlone()
    {
        Assert.SkipUnless(Tiered, "tiered compilation disabled");
        bool wasEnabled = Reset();
        try
        {
            Assert.True(SharedGenericRetarget.Enable(PatcherShared(), out string? error), error);
            int before = SharedGenericRetarget.Retargets;
            int[] hits = Intercepts(MethodOf(typeof(CommonBasePartsManager<Part>), "GetHashString"));
            Assert.Contains(hits, h => h < CallsPerRound); // lost like vanilla MonoMod: not ours to repair
            Assert.Equal(before, SharedGenericRetarget.Retargets);
        }
        finally
        {
            Restore(wasEnabled);
        }
    }

    /// <summary>Guard: every shared generic target the mod patches keeps intercepting through sustained repeated calls.</summary>
    [Fact]
    public void EverySharedGenericPatcherTargetSurvivesSustainedTiering()
    {
        bool wasEnabled = Reset();
        try
        {
            MethodBase[] shared = PatcherShared();
            Assert.Equal(2, shared.Length); // BoundingPoints, StatusTempDispose<Part>; update when adding one
            Assert.True(SharedGenericRetarget.Enable(shared, out string? error), error);
            foreach (MethodBase target in shared)
            {
                int[] hits = Intercepts(target);
                Assert.True(hits.All(h => h == CallsPerRound), $"{target.DeclaringType}.{target.Name}: hits per round {string.Join(",", hits)}");
            }
            Assert.False(SharedGenericRetarget.Failed);
        }
        finally
        {
            Restore(wasEnabled);
        }
    }

    [Fact]
    public void EnableRejectsFamiliesSharingCodeAndDifferentFamiliesWhileEnabled()
    {
        bool wasEnabled = Reset();
        try
        {
            Assert.False(SharedGenericRetarget.Enable([BoundingPoints.Target, BlueprintBoundingPoints], out string? error));
            Assert.Contains("share code", error);
            Assert.False(SharedGenericRetarget.IsEnabled);
            Assert.True(SharedGenericRetarget.Enable([BoundingPoints.Target], out error), error);
            Assert.True(SharedGenericRetarget.Enable([BoundingPoints.Target], out error), error);
            Assert.False(SharedGenericRetarget.Enable(PatcherShared(), out _));
            Assert.True(SharedGenericRetarget.Disable());
            Assert.True(SharedGenericRetarget.Disable());
            Assert.False(SharedGenericRetarget.IsEnabled);
        }
        finally
        {
            Restore(wasEnabled);
        }
    }

    [Fact]
    public void ForwardsOnlyTheRegisteredFamilyKey()
    {
        var calls = new List<MethodBase?>();
        RunSynthetic([BoundingPoints.Target], (_, m, _, _, _) => calls.Add(m), related =>
        {
            related[BoundingPoints.Target] = new object();
            related[MethodOf(typeof(CommonBasePartsManager<Part>), "HasPart")] = new object(); // other code, ignored
            Compile(CanonicalBoundingPoints);
            Compile(MethodOf(typeof(CommonBasePartsManager<>).MakeGenericType(CanonType), "HasPart")); // not a family
            Compile(BoundingPoints.Target); // exact key: MonoMod's own handler
        });
        Assert.Equal([BoundingPoints.Target], calls);
        Assert.False(SharedGenericRetarget.Failed);
    }

    [Fact]
    public void ForeignKeyOnFamilyCodeWithoutOurPatchIsNotForwarded()
    {
        var calls = new List<MethodBase?>();
        RunSynthetic([BoundingPoints.Target], (_, m, _, _, _) => calls.Add(m), related =>
        {
            related[BlueprintBoundingPoints] = new object(); // another mod, our family not patched
            Compile(CanonicalBoundingPoints);
        });
        Assert.Empty(calls);
        Assert.False(SharedGenericRetarget.Failed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConflictingAliasFailsTheFamilyOnce(bool canonicalKey)
    {
        var calls = new List<MethodBase?>();
        List<MethodBase> failed = [];
        RunSynthetic([BoundingPoints.Target], (_, m, _, _, _) => calls.Add(m), related =>
        {
            related[BoundingPoints.Target] = new object();
            related[canonicalKey ? CanonicalBoundingPoints : BlueprintBoundingPoints] = new object();
            Compile(CanonicalBoundingPoints);
            Compile(CanonicalBoundingPoints);
            Assert.True(SharedGenericRetarget.Failed);
            failed = SharedGenericRetarget.TakeFailed();
        });
        Assert.Empty(calls);
        Assert.Equal([BoundingPoints.Target], failed);
        Assert.False(SharedGenericRetarget.Failed);
    }

    [Fact]
    public void ThrowingRetargetFailsTheFamily()
    {
        List<MethodBase> failed = [];
        RunSynthetic([BoundingPoints.Target], (_, _, _, _, _) => throw new InvalidOperationException("boom"), related =>
        {
            related[BoundingPoints.Target] = new object();
            Compile(CanonicalBoundingPoints);
            failed = SharedGenericRetarget.TakeFailed();
        });
        Assert.Equal([BoundingPoints.Target], failed);
    }

    /// <summary>The next-frame fallback removes this mod's patch from a failed family; the target runs vanilla.</summary>
    [Fact]
    public void FallbackUnpatchesFailedFamilyOutsideTheCallback()
    {
        var harmony = new Harmony(Patcher.HarmonyId);
        try
        {
            harmony.Patch(BoundingPoints.Target, transpiler: new HarmonyMethod(typeof(BoundingPoints), nameof(BoundingPoints.Transpiler)));
            RunSynthetic([BoundingPoints.Target], (_, _, _, _, _) => { }, related =>
            {
                related[BoundingPoints.Target] = new object();
                related[BlueprintBoundingPoints] = new object();
                Compile(CanonicalBoundingPoints);
                Assert.True(SharedGenericRetarget.Failed);
                Assert.Contains(Patcher.HarmonyId, Harmony.GetPatchInfo(BoundingPoints.Target)!.Owners); // nothing unpatched inside the callback
                Patcher.ApplyRetargetFallback();
            });
            Assert.False(SharedGenericRetarget.Failed);
            Assert.DoesNotContain(Patcher.HarmonyId, Harmony.GetPatchInfo(BoundingPoints.Target)?.Owners ?? []);
        }
        finally
        {
            harmony.UnpatchAll(Patcher.HarmonyId);
        }
    }

    /// <summary>
    /// A failed family that cannot be unpatched fails the frame before the native update runs (game's fatal path), without a
    /// full rollback; the other failed families are still removed.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void UnremovableFailedFamilyStopsTheNativeUpdate(int families)
    {
        MethodBase status = StatusTempDispose.Targets.Single(t => t.Target.DeclaringType == typeof(StatusHandler<Part>)).Target;
        MethodBase[] registered = families == 1 ? [BoundingPoints.Target] : [BoundingPoints.Target, status];
        MethodBase canonicalStatus = MethodOf(typeof(StatusHandler<>).MakeGenericType(CanonType), status.Name);
        var attempts = new List<MethodBase>();
        var harmony = new Harmony(TestId);
        _nativeUpdates = 0;
        try
        {
            harmony.Patch(AccessTools.Method(typeof(SharedGenericRetargetTests), nameof(NativeUpdate)),
                prefix: new HarmonyMethod(typeof(BlueprintEvaluationScope), nameof(BlueprintEvaluationScope.Prefix)),
                finalizer: new HarmonyMethod(typeof(BlueprintEvaluationScope), nameof(BlueprintEvaluationScope.Finalizer)));
            Patcher.OverrideUnpatchFamily(target =>
            {
                attempts.Add(target);
                if (attempts.Count == 1) throw new InvalidOperationException("unpatch failed");
            });
            RunSynthetic(registered, (_, _, _, _, _) => { }, related =>
            {
                related[BoundingPoints.Target] = new object();
                related[BlueprintBoundingPoints] = new object();
                related[status] = new object();
                related[MethodOf(typeof(StatusHandler<>).MakeGenericType(typeof(BlueprintPart)), status.Name)] = new object();
                Compile(CanonicalBoundingPoints);
                Compile(canonicalStatus);
                Assert.True(SharedGenericRetarget.Failed);
                var fatal = Assert.Throws<InvalidOperationException>(NativeUpdate);
                Assert.Contains("Performance mod", fatal.Message);
                Assert.False(SharedGenericRetarget.IsEnabled);
            });
            Assert.Equal(0, _nativeUpdates);
            Assert.Equal(families, attempts.Count); // best effort: every failed family attempted
            Assert.False(SharedGenericRetarget.Failed);
        }
        finally
        {
            Patcher.OverrideUnpatchFamily(null);
            harmony.UnpatchAll(TestId);
        }
    }

    private static int _nativeUpdates;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void NativeUpdate() => _nativeUpdates++;
    /// <summary>After a failed Apply rolls back, a later canonical compile forwards nothing.</summary>
    [Fact]
    public void RollBackDisablesForwardingBeforeUnpatching()
    {
        var calls = new List<MethodBase?>();
        bool disabledBeforeUnpatch = false;
        RunSynthetic([BoundingPoints.Target], (_, m, _, _, _) => calls.Add(m), related =>
        {
            related[BoundingPoints.Target] = new object();
            Patcher.RollBack(() => disabledBeforeUnpatch = !SharedGenericRetarget.IsEnabled);
            Compile(CanonicalBoundingPoints);
        });
        Assert.True(disabledBeforeUnpatch);
        Assert.Empty(calls);
    }

    /// <summary>Compiles racing patch/unpatch (registry changes) and disable/enable: no exception, no false failure, nothing after disable.</summary>
    [Fact]
    public void ConcurrentCompileUnpatchAndDisableAreSafe()
    {
        bool wasEnabled = Reset();
        var related = new System.Collections.Concurrent.ConcurrentDictionary<MethodBase, object>();
        SharedGenericRetarget.Compiled recorder = (_, _, _, _, _) => Interlocked.Increment(ref _forwarded);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        long deadline = Environment.TickCount64 + 600;
        void Loop(Action body) { try { while (Environment.TickCount64 < deadline) body(); } catch (Exception ex) { errors.Enqueue(ex); } }
        try
        {
            SharedGenericRetarget.EnableForTest([BoundingPoints.Target], related, recorder);
            var threads = new List<Thread>();
            for (int i = 0; i < 4; i++) threads.Add(new Thread(() => Loop(() => Compile(CanonicalBoundingPoints))));
            threads.Add(new Thread(() => Loop(() => { related[BoundingPoints.Target] = 0; related.TryRemove(BoundingPoints.Target, out _); })));
            threads.Add(new Thread(() => Loop(() =>
            {
                Assert.True(SharedGenericRetarget.Disable());
                SharedGenericRetarget.EnableForTest([BoundingPoints.Target], related, recorder);
            })));
            threads.ForEach(t => t.Start());
            threads.ForEach(t => Assert.True(t.Join(TimeSpan.FromSeconds(10)), "deadlock"));
            Assert.Empty(errors);
            Assert.False(SharedGenericRetarget.Failed);
            Assert.True(SharedGenericRetarget.Disable());
            related[BoundingPoints.Target] = 0;
            int after = Volatile.Read(ref _forwarded);
            Compile(CanonicalBoundingPoints);
            Assert.Equal(after, Volatile.Read(ref _forwarded));
        }
        finally
        {
            Restore(wasEnabled);
        }
    }

    private static readonly Type CanonType = typeof(object).Assembly.GetType("System.__Canon")!;
    private static MethodBase CanonicalBoundingPoints => MethodOf(typeof(CommonBasePartsManager<>).MakeGenericType(CanonType), "GetBoundingPoints");
    private static MethodBase BlueprintBoundingPoints => MethodOf(typeof(CommonBasePartsManager<BlueprintPart>), "GetBoundingPoints");

    private static MethodBase[] PatcherShared() => Patcher.Patches.Select(p => p.Target).Where(SharedGenericRetarget.IsShared).Distinct().ToArray();

    private static void Compile(MethodBase method) => SharedGenericRetarget.OnCompiled(default, method, 0x1000, 0x1000, 16);

    /// <summary>Listener logic over a fake detour registry and recorder; the process-wide listener is restored afterwards.</summary>
    private static void RunSynthetic(MethodBase[] families, SharedGenericRetarget.Compiled retarget, Action<System.Collections.Concurrent.ConcurrentDictionary<MethodBase, object>> body)
    {
        bool wasEnabled = Reset();
        try
        {
            var related = new System.Collections.Concurrent.ConcurrentDictionary<MethodBase, object>();
            SharedGenericRetarget.EnableForTest(families, related, retarget);
            body(related);
        }
        finally
        {
            Restore(wasEnabled);
        }
    }

    private static bool Reset()
    {
        bool wasEnabled = SharedGenericRetarget.IsEnabled;
        Assert.True(SharedGenericRetarget.Disable());
        _ = SharedGenericRetarget.TakeFailed();
        return wasEnabled;
    }

    /// <summary>Back to the state other tests (Patcher.Apply) leave: the real listener for the mod's shared targets, if it was on.</summary>
    private static void Restore(bool wasEnabled)
    {
        Assert.True(SharedGenericRetarget.Disable());
        _ = SharedGenericRetarget.TakeFailed();
        if (wasEnabled) Assert.True(SharedGenericRetarget.Enable(PatcherShared(), out string? error), error);
    }
    /// <summary>Patches <paramref name="target"/> with a counting prefix that skips the body, calls it in rounds with pauses (tier-up); returns intercepted calls per round.</summary>
    private static int[] Intercepts(MethodBase target)
    {
        Action call = Invoker(target);
        var harmony = new Harmony(TestId);
        var hits = new int[Rounds];
        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(SharedGenericRetargetTests), nameof(Skip)));
            for (int round = 0; round < Rounds; round++)
            {
                Volatile.Write(ref _hits, 0);
                for (int i = 0; i < CallsPerRound; i++)
                {
                    try
                    {
                        call();
                    }
                    catch (NullReferenceException)
                    {
                        // not intercepted: the vanilla body ran on the null instance
                    }
                }
                hits[round] = Volatile.Read(ref _hits);
                Thread.Sleep(PauseMs);
            }
        }
        finally
        {
            harmony.UnpatchAll(TestId);
        }
        return hits;
    }

    private static bool Skip()
    {
        Interlocked.Increment(ref _hits);
        return false;
    }

    /// <summary>Direct IL call of <paramref name="target"/> with null/default arguments (the prefix skips the body).</summary>
    private static Action Invoker(MethodBase target)
    {
        var dm = new DynamicMethod("Call_" + target.Name, typeof(void), Type.EmptyTypes, typeof(SharedGenericRetargetTests).Module, true);
        ILGenerator il = dm.GetILGenerator();
        Assert.False(target.DeclaringType!.IsValueType);
        if (!target.IsStatic) il.Emit(OpCodes.Ldnull);
        foreach (ParameterInfo p in target.GetParameters())
        {
            Type t = p.ParameterType;
            if (t.IsByRef)
                il.Emit(OpCodes.Ldloca, il.DeclareLocal(t.GetElementType()!));
            else if (!t.IsValueType)
                il.Emit(OpCodes.Ldnull);
            else
                il.Emit(OpCodes.Ldloc, il.DeclareLocal(t));
        }
        il.Emit(OpCodes.Call, (MethodInfo)target);
        if (((MethodInfo)target).ReturnType != typeof(void)) il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);
        return dm.CreateDelegate<Action>();
    }

    private static MethodBase MethodOf(Type type, string name) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Single(m => m.Name == name);
}
