using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Cosmoteer.Ships;
using HarmonyLib;
using Xunit;
using Phase = UnofficialPatch.ViewDetect.Phase;

namespace UnofficialPatch.Tests;

[Collection(nameof(ViewDetectTests))]
public class ViewDetectTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1", true)]
    public void ExplicitOptInOnly(string? value, bool expected) => Assert.Equal(expected, ViewDetect.IsRequested(value));

    [Fact]
    public void OffModeInstallsNothing()
    {
        Assert.Equal(ViewDetect.IsRequested(Environment.GetEnvironmentVariable("UNOFFICIAL_PATCH_VIEW_DETECT")), ViewDetect.Requested);
        Assert.False(ViewDetect.Requested); // the test run must not set the opt-in
        Assert.Empty(ViewDetect.Patches);
        Type[] brackets = ViewDetect.Targets.Select(t => t.Patch).Distinct().ToArray();
        Assert.DoesNotContain(Patcher.Patches, p => brackets.Contains(p.Patch));
        Assert.True(ViewDetect.Initialize(new Harmony("morgott.unofficialpatch.viewdetect-off")));
        Assert.Null(ViewDetect.Active);

        MethodBase[] watched = ViewDetect.Targets.Select(t => t.Target)
            .Concat(ViewDetect.Detector.Resolve(ViewDetect.DefaultGuards, 0).Guards.Select(g => g.Method)).ToArray();
        try
        {
            Assert.True(Patcher.Apply());
            foreach (MethodBase method in watched)
            {
                Patches? info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                Assert.DoesNotContain(info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers), p =>
                    p.PatchMethod.Module.Assembly.IsDynamic || p.PatchMethod.DeclaringType?.DeclaringType == typeof(ViewDetect));
            }
            Assert.Null(ViewDetect.Active);
        }
        finally
        {
            new Harmony(Patcher.HarmonyId).UnpatchAll(Patcher.HarmonyId);
        }
    }

    [Fact]
    public void BracketTargetsResolveAndArePatchable()
    {
        (MethodBase Target, Type Patch)[] targets = ViewDetect.Targets;
        Assert.Equal(16, targets.Length);
        Assert.All(targets, t =>
        {
            Assert.False(SharedGenericRetarget.IsShared(t.Target), t.Target.Name);
            Assert.NotNull(t.Target.GetMethodBody());
            _ = Patcher.FindR2RInliners(t.Target); // throws on a shared generic inliner
            Assert.NotNull(t.Patch.GetMethod("Prefix"));
            Assert.NotNull(t.Patch.GetMethod("Finalizer"));
        });
        try
        {
            var harmony = new Harmony("morgott.unofficialpatch.viewdetect-brackets");
            foreach ((MethodBase target, Type patch) in targets)
                harmony.Patch(target, new HarmonyMethod(patch.GetMethod("Prefix")), finalizer: new HarmonyMethod(patch.GetMethod("Finalizer")));
        }
        finally
        {
            new Harmony("morgott.unofficialpatch.viewdetect-brackets").UnpatchAll("morgott.unofficialpatch.viewdetect-brackets");
        }
    }

    [Fact]
    public void DefaultGuardsResolveWithoutSharedGenerics()
    {
        ViewDetect.Detector detector = ViewDetect.Detector.Resolve(ViewDetect.DefaultGuards, 0);
        // Declared on CommonBasePartsManager<Part>: shared code, skipped and listed.
        Assert.Equal("Cosmoteer.Ships.Parts.PartsManager::get_BoundingRect", Assert.Single(detector.Skipped).Name);
        Assert.Equal(29, detector.Guards.Length);
        Assert.All(detector.Guards, g =>
        {
            Assert.False(SharedGenericRetarget.IsShared(g.Method), g.Name);
            Assert.True(g.IlBytes > 0, g.Name);
        });
    }

    [Fact]
    public void ResolveSkipsSharedGenericMissingBodylessAndDuplicates()
    {
        MethodInfo parts = AccessTools.PropertyGetter(typeof(Ship), nameof(Ship.Parts));
        ViewDetect.Detector detector = ViewDetect.Detector.Resolve(
        [
            ("Cosmoteer.Ships.Ship::get_Parts", parts),
            ("List<string>::get_Count", AccessTools.PropertyGetter(typeof(List<string>), nameof(List<string>.Count))),
            ("missing", null),
            ("Stream::Read", AccessTools.Method(typeof(Stream), nameof(Stream.Read), [typeof(byte[]), typeof(int), typeof(int)])),
            ("again", parts),
        ], 0);
        Assert.Single(detector.Guards);
        Assert.Equal(
        new (string, string)[]
        {
            ("List<string>::get_Count", "shared generic (__Canon body run by other instantiations)"),
            ("missing", "not found"),
            ("Stream::Read", "no IL body"),
            ("again", "duplicate of Cosmoteer.Ships.Ship::get_Parts"),
        }, detector.Skipped);
    }

    [Fact]
    public void PhaseFlagNestsRestoresAndIgnoresOtherThreads()
    {
        ViewDetect.BindMainThread(Environment.CurrentManagedThreadId);
        ViewDetect.ResetPhase();
        try
        {
            int update = ViewDetect.Enter(Phase.Update);
            Assert.Equal((int)Phase.None + 1, update); // entered token = previous + 1
            Assert.True(ViewDetect.IsRender(ViewDetect.Current));
            int tick = ViewDetect.Enter(Phase.Tick);
            Assert.Equal((int)Phase.Update + 1, tick);
            Assert.False(ViewDetect.IsRender(ViewDetect.Current));
            Assert.Equal(0, OnThread(null, () => ViewDetect.Enter(Phase.Draw))); // not the main thread: no change
            Assert.Equal(Phase.Tick, ViewDetect.Current);
            ViewDetect.Exit(0); // token of a scope never entered: no change
            Assert.Equal(Phase.Tick, ViewDetect.Current);
            ViewDetect.Exit(tick);
            Assert.Equal(Phase.Update, ViewDetect.Current);

            // Pool workers inherit the main thread's phase; any other thread sees None.
            Assert.Equal(Phase.Update, OnThread(ViewDetect.FastParallelThreadName, ViewDetect.EffectivePhase));
            Assert.Equal(Phase.None, OnThread("Some Thread", ViewDetect.EffectivePhase));
            Assert.Equal(Phase.None, OnThread(null, ViewDetect.EffectivePhase));

            // The finalizer restores on exception and passes it through.
            ViewDetect.GuiBracket.Prefix(out int gui);
            Assert.Equal(Phase.Gui, ViewDetect.Current);
            var thrown = new InvalidOperationException();
            Assert.Same(thrown, ViewDetect.GuiBracket.Finalizer(gui, thrown));
            Assert.Equal(Phase.Update, ViewDetect.Current);
            ViewDetect.Exit(update);
            Assert.Equal(Phase.None, ViewDetect.Current);
            Assert.Equal(1, ViewDetect.Entries(Phase.Update));
            Assert.Equal(1, ViewDetect.Entries(Phase.Tick));
            Assert.Equal(1, ViewDetect.Entries(Phase.Gui));
            Assert.Equal(0, ViewDetect.Entries(Phase.Draw));
        }
        finally
        {
            ViewDetect.ResetPhase();
        }
    }

    [Fact]
    public void GuardCountsOnlyRenderPhasesAndSeparatesOtherThreads()
    {
        ViewDetect.BindMainThread(Environment.CurrentManagedThreadId);
        ViewDetect.ResetPhase();
        ViewDetect.Detector detector = Fake(2);
        ViewDetect.Activate(detector);
        try
        {
            ViewDetect.Guard(0); // None
            int tick = ViewDetect.Enter(Phase.Tick);
            ViewDetect.Guard(0);
            ViewDetect.Exit(tick);
            int draw = ViewDetect.Enter(Phase.Draw);
            ViewDetect.Guard(0);
            ViewDetect.Guard(0);
            ViewDetect.Guard(1);
            ViewDetect.Guard(7); // unknown id: ignored
            OnThread("Some Thread", () => { ViewDetect.Guard(1); return 0; });
            OnThread(ViewDetect.FastParallelThreadName, () => { ViewDetect.Guard(1); return 0; });
            ViewDetect.Exit(draw);
            ViewDetect.Guard(1); // None again

            Assert.Equal(2, detector.Total(0));
            Assert.Equal(2, detector.PhaseHits(0, Phase.Draw));
            Assert.Equal(2, detector.Total(1)); // main + pool worker
            Assert.Equal(1, detector.OtherThread(1));
            Assert.Equal(0, detector.PhaseHits(1, Phase.Update));
        }
        finally
        {
            Deactivate(detector);
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(ViewDetect.FullSampleHits, true)]
    [InlineData(ViewDetect.FullSampleHits + 1, false)]
    [InlineData(ViewDetect.FullSampleHits + ViewDetect.SampleEvery - 1, false)]
    [InlineData(ViewDetect.FullSampleHits + ViewDetect.SampleEvery, true)]
    public void SamplingKeepsEarlyHitsThenOneInN(long hit, bool sampled) => Assert.Equal(sampled, ViewDetect.ShouldSample(hit));

    [Fact]
    public void SamplesDeduplicateByCallerAndKeepFirstStacks()
    {
        ViewDetect.Detector detector = Fake(1);
        int stackCalls = 0;
        string Stack(string caller) { stackCalls++; return "    at " + caller + "\n"; }
        detector.RecordSample(0, "A::x", () => Stack("A::x"));
        detector.RecordSample(0, "A::x", () => Stack("A::x"));
        detector.RecordSample(0, "B::y", () => Stack("B::y"));
        detector.RecordSample(0, "A::x", () => Stack("A::x"));
        Assert.Equal(new (string, long)[] { ("A::x", 3L), ("B::y", 1L) }, detector.Callers(0));
        Assert.Equal(2, stackCalls); // a known caller never builds a stack
        Assert.Equal(new[] { "A::x", "B::y" }, detector.Stacks(0).Select(s => s.Caller));

        for (int i = 0; i < 20; i++) detector.RecordSample(0, "C::z" + i, () => Stack("C"));
        Assert.Equal(22, detector.Callers(0).Count);
        Assert.Equal(ViewDetect.Detector.MaxStacksPerGuard, detector.Stacks(0).Count);
    }

    [Fact]
    public void ReportListsCountsCallersStacksAndSkipped()
    {
        ViewDetect.BindMainThread(Environment.CurrentManagedThreadId);
        ViewDetect.Detector detector = Fake(2, [("X::get_Shared", "shared generic (__Canon body run by other instantiations)")]);
        detector.Guards[1].Installed = false;
        detector.Guards[1].Failure = "patch failed: boom";
        detector.Count(0, (int)Phase.Draw);
        detector.Count(0, (int)Phase.Draw);
        detector.Count(0, (int)Phase.Update);
        detector.CountOtherThread(0);
        detector.RecordSample(0, "Caller::M", () => "    at Caller::M\n    at Root::Run\n");
        long[] entries = [0, 5, 1, 7, 7, 7, 21];

        string report = detector.FormatReport(new DateTime(2026, 11, 1, 12, 0, 0), entries);

        Assert.StartsWith("# Unofficial Patch view-detect report\n", report);
        Assert.Contains("Guards installed 1, skipped 2.", report);
        Assert.Contains("| Tick | 5 |\n", report);
        Assert.Contains("| Gui | 21 |\n", report);
        Assert.Contains("| 1 | `G0::get_A` | 3 | 1 | 0 | 2 | 0 | 1 | 1 | 7 | 0 | always |\n", report);
        Assert.Contains("| 2 | `G1::get_A` (not installed) | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 7 | 0 | always |\n", report);
        Assert.Contains("### `G0::get_A` (3 hits, 1 sampled)\n\n| Caller | Sampled hits |\n|---|---:|\n| `Caller::M` | 1 |\n", report);
        Assert.Contains("First stack from `Caller::M`:\n\n```\n    at Caller::M\n    at Root::Run\n```\n", report);
        Assert.DoesNotContain("### `G1::get_A`", report);
        Assert.Contains("| `X::get_Shared` | shared generic (__Canon body run by other instantiations) |\n", report);
        Assert.Contains("| `G1::get_A` | patch failed: boom |\n", report);
    }

    [Fact]
    public void InstalledGuardCountsRenderReadsOfARealGetterWithItsCaller()
    {
        const string id = "morgott.unofficialpatch.viewdetect-test";
        var harmony = new Harmony(id);
        ViewDetect.Detector detector = ViewDetect.Detector.Resolve(["Cosmoteer.Ships.Ship::get_Parts"], Environment.ProcessId);
        ViewDetect.BindMainThread(Environment.CurrentManagedThreadId);
        ViewDetect.ResetPhase();
        var ship = (Ship)RuntimeHelpers.GetUninitializedObject(typeof(Ship));
        try
        {
            ViewDetect.Install(harmony, detector, EmitForTest(detector.Guards.Length));
            Assert.True(detector.Guards[0].Installed, detector.Guards[0].Failure);
            // The emitted guard assembly must not pause BlueprintToggleUpdates (its scan refuses unknown dynamic code).
            Assert.True(BlueprintToggleUpdates.IsKnownDynamic(detector.Guards[0].Postfix!.Module.Assembly));
            ViewDetect.Activate(detector);

            int tick = ViewDetect.Enter(Phase.Tick);
            Assert.Null(ReadParts(ship));
            ViewDetect.Exit(tick);
            Assert.Equal(0, detector.Total(0));

            int draw = ViewDetect.Enter(Phase.Draw);
            Assert.Null(ReadParts(ship));
            ViewDetect.Exit(draw);
            Assert.True(detector.PhaseHits(0, Phase.Draw) >= 1);
            string caller = typeof(ViewDetectTests).FullName + "::" + nameof(ReadParts);
            Assert.Contains(detector.Callers(0), c => c.Caller == caller);
            Assert.Contains(detector.Stacks(0), s => s.Caller == caller && s.Stack.Contains(nameof(InstalledGuardCountsRenderReadsOfARealGetterWithItsCaller)));
        }
        finally
        {
            ViewDetect.Uninstall(harmony, detector);
            harmony.UnpatchAll(id);
            Deactivate(detector);
        }
        Assert.Null(Harmony.GetPatchInfo(detector.Guards[0].Method)?.Postfixes.FirstOrDefault(p => p.owner == id));
    }

    [Fact]
    public void BracketsRestoreOnlyEnteredScopesWhenAnEarlierPrefixSkipsOrThrows()
    {
        const string ours = "morgott.unofficialpatch.viewdetect-nested";
        const string foreign = "foreign.viewdetect-nested";
        ViewDetect.BindMainThread(Environment.CurrentManagedThreadId);
        ViewDetect.ResetPhase();
        try
        {
            var harmony = new Harmony(ours);
            harmony.Patch(Own(nameof(Outer)), Hook(typeof(ViewDetect.UpdateBracket), "Prefix"), finalizer: Hook(typeof(ViewDetect.UpdateBracket), "Finalizer"));
            foreach (string inner in new[] { nameof(InnerSkipped), nameof(InnerThrows) })
                harmony.Patch(Own(inner), Hook(typeof(ViewDetect.TickBracket), "Prefix"), finalizer: Hook(typeof(ViewDetect.TickBracket), "Finalizer"));
            // Foreign prefixes ordered before ours: one skips the original, one throws (ours then never runs).
            var other = new Harmony(foreign);
            other.Patch(Own(nameof(InnerSkipped)), new HarmonyMethod(Own(nameof(SkipPrefix)), Priority.First));
            other.Patch(Own(nameof(InnerThrows)), new HarmonyMethod(Own(nameof(ThrowPrefix)), Priority.First));

            (Phase afterSkip, Phase afterThrow) = Outer();

            Assert.Equal(Phase.Update, afterSkip);
            Assert.Equal(Phase.Update, afterThrow); // the old code reset the enclosing Update scope to None here
            Assert.Equal(Phase.None, ViewDetect.Current);
        }
        finally
        {
            new Harmony(foreign).UnpatchAll(foreign);
            new Harmony(ours).UnpatchAll(ours);
            ViewDetect.ResetPhase();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Phase AfterSkip, Phase AfterThrow) Outer()
    {
        InnerSkipped();
        Phase afterSkip = ViewDetect.Current;
        try
        {
            InnerThrows();
        }
        catch (InvalidOperationException)
        {
        }
        return (afterSkip, ViewDetect.Current);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InnerSkipped() => Environment.SetEnvironmentVariable("UNOFFICIAL_PATCH_VIEW_DETECT_TEST_UNUSED", null);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InnerThrows() => Environment.SetEnvironmentVariable("UNOFFICIAL_PATCH_VIEW_DETECT_TEST_UNUSED", null);

    public static bool SkipPrefix() => false;
    public static void ThrowPrefix() => throw new InvalidOperationException("foreign prefix");

    private static MethodInfo Own(string name) => AccessTools.Method(typeof(ViewDetectTests), name);
    private static HarmonyMethod Hook(Type patch, string name) => new(patch.GetMethod(name));

    /// <summary>
    /// Guard-named assemblies this class creates (foreign fixtures and test-emitted guards). Only the tests' scan hook
    /// (PortToggleModeTests.IsTestDynamic) and the "every dynamic assembly is known" sweep skip them, so the process-wide
    /// BlueprintToggleUpdates watcher, installed by earlier tests, is not failed by them. Production has no such exemption:
    /// ViewDetect.Prepare emits before the watcher exists.
    /// </summary>
    private static readonly List<Assembly> TestGuardNamed = [];
    [ThreadStatic] private static bool _creatingTestAssembly;

    internal static bool IsForeignFixture(Assembly assembly)
    {
        // The load event fires before the instance can be recorded.
        if (_creatingTestAssembly && assembly.IsDynamic && assembly.GetName().Name == ViewDetect.GuardAssemblyName) return true;
        lock (TestGuardNamed) return TestGuardNamed.Any(f => ReferenceEquals(f, assembly));
    }

    /// <summary>ViewDetect.EmitGuards under the tests' scan exemption (see <see cref="TestGuardNamed"/>).</summary>
    private static MethodInfo[] EmitForTest(int count)
    {
        bool outer = _creatingTestAssembly;
        _creatingTestAssembly = true;
        try
        {
            MethodInfo[] guards = ViewDetect.EmitGuards(count);
            lock (TestGuardNamed) TestGuardNamed.Add(guards[0].Module.Assembly);
            return guards;
        }
        finally
        {
            _creatingTestAssembly = outer;
        }
    }

    [Fact]
    public void GuardAssemblyIsTrustedByInstanceNotName()
    {
        (AssemblyBuilder before, Assembly beforeRuntime) = ForeignNamedLikeGuards();
        Assert.False(ViewDetect.IsGuardAssembly(before));
        Assert.False(ViewDetect.IsGuardAssembly(beforeRuntime));
        Assert.NotNull(BlueprintToggleUpdates.ScanAssembly(before));
        Assert.NotNull(BlueprintToggleUpdates.ScanAssembly(beforeRuntime));

        // A reentrant AssemblyLoad handler loads a same-name foreign assembly while the guards are emitted.
        var reentrant = new List<(AssemblyBuilder Builder, Assembly Runtime)>();
        int thread = Environment.CurrentManagedThreadId;
        AssemblyLoadEventHandler handler = (_, e) =>
        {
            if (Environment.CurrentManagedThreadId != thread || reentrant.Count != 0 || e.LoadedAssembly.GetName().Name != ViewDetect.GuardAssemblyName) return;
            reentrant.Add(default);
            reentrant[0] = ForeignNamedLikeGuards();
        };
        AppDomain.CurrentDomain.AssemblyLoad += handler;
        MethodInfo[] guards;
        try
        {
            guards = EmitForTest(1);
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyLoad -= handler;
        }
        (AssemblyBuilder slipped, Assembly slippedRuntime) = Assert.Single(reentrant); // the reentrant path ran
        Assert.False(ViewDetect.IsGuardAssembly(slipped));
        Assert.False(ViewDetect.IsGuardAssembly(slippedRuntime));
        Assert.NotNull(BlueprintToggleUpdates.ScanAssembly(slipped));
        Assert.NotNull(BlueprintToggleUpdates.ScanAssembly(slippedRuntime));

        Assert.True(ViewDetect.IsGuardAssembly(guards[0].Module.Assembly));
        Assert.Null(BlueprintToggleUpdates.ScanAssembly(guards[0].Module.Assembly));
        Assert.True(BlueprintToggleUpdates.IsKnownDynamic(guards[0].DeclaringType!.Assembly));

        (AssemblyBuilder after, Assembly afterRuntime) = ForeignNamedLikeGuards();
        Assert.False(ViewDetect.IsGuardAssembly(after));
        Assert.NotNull(BlueprintToggleUpdates.ScanAssembly(afterRuntime));
        Assert.NotNull(BlueprintToggleUpdates.ScanAssembly(before));
    }

    /// <summary>A foreign assembly with the guard name: its builder and the runtime assembly behind it (GetAssemblies' view).</summary>
    private static (AssemblyBuilder Builder, Assembly Runtime) ForeignNamedLikeGuards()
    {
        AssemblyBuilder assembly;
        Type type;
        bool outer = _creatingTestAssembly;
        _creatingTestAssembly = true;
        try
        {
            assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(ViewDetect.GuardAssemblyName), AssemblyBuilderAccess.Run);
            type = assembly.DefineDynamicModule("Foreign").DefineType("Foreign", TypeAttributes.Public).CreateType();
        }
        finally
        {
            _creatingTestAssembly = outer;
        }
        lock (TestGuardNamed)
        {
            TestGuardNamed.Add(assembly);
            TestGuardNamed.Add(type.Assembly);
        }
        return (assembly, type.Assembly);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Cosmoteer.Ships.Parts.PartsManager ReadParts(Ship ship) => ship.Parts;

    private static ViewDetect.Detector Fake(int guards, List<(string, string)>? skipped = null)
    {
        MethodInfo method = AccessTools.PropertyGetter(typeof(Ship), nameof(Ship.Parts));
        var infos = Enumerable.Range(0, guards).Select(i => new ViewDetect.GuardInfo($"G{i}::get_A", method, 7, 0) { Installed = true }).ToArray();
        return new ViewDetect.Detector(infos, skipped ?? [], 0);
    }

    /// <summary>Disables without leaving a report file next to the test DLL.</summary>
    private static void Deactivate(ViewDetect.Detector detector)
    {
        ViewDetect.Disable();
        ViewDetect.ResetPhase();
        string path = ViewDetect.ReportPath(detector);
        File.Delete(path);
        File.Delete(path + ".tmp");
    }

    private static T OnThread<T>(string? name, Func<T> body)
    {
        T result = default!;
        var thread = new Thread(() => result = body());
        if (name != null) thread.Name = name;
        thread.Start();
        thread.Join();
        return result;
    }
}
