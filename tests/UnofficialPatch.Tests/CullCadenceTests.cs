using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Simulation;
using Halfling;
using Halfling.Application;
using HarmonyLib;
using Xunit;

namespace UnofficialPatch.Tests;

public class CullCadenceTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1", true)]
    public void ExplicitOptInOnly(string? value, bool expected) => Assert.Equal(expected, CullCadence.IsRequested(value));

    [Theory]
    [InlineData(0, 1206)]
    [InlineData(-1, 1206)]
    [InlineData(-30, 0)]
    [InlineData(int.MinValue, 17)]
    public void InvalidPeriodsAndUnscopedCallsKeepNativeMax(int period, int count)
    {
        Assert.Equal(Mathx.Max(period, count), CullCadence.SelectBound(true, period, count));
        Assert.Equal(Mathx.Max(period, count), CullCadence.SelectBound(false, period, count));
    }

    [Fact]
    public void StableMembershipChecksAllIndicesWithinOnePeriod()
    {
        const int period = 30, count = 1206;
        var visited = new HashSet<int>();
        int cursor = 0;
        for (int tick = 0; tick < period; tick++)
            foreach (int index in Scan(ref cursor, period, count, true)) visited.Add(index);
        Assert.Equal(count, visited.Count);
        cursor = 0;
        for (int tick = 0; tick < count - 1; tick++) Assert.DoesNotContain(0, Scan(ref cursor, period, count, false));
        Assert.Contains(0, Scan(ref cursor, period, count, false));
    }

    [Fact]
    public void ContinualAppendsCannotKeepLowResidueStarved()
    {
        int oldCursor = 0, newCursor = 0, firstNewHit = -1;
        for (int tick = 1; tick <= 300; tick++)
        {
            int count = 1206 + tick;
            Assert.DoesNotContain(0, Scan(ref oldCursor, 30, count, false));
            if (Scan(ref newCursor, 30, count, true).Contains(0) && firstNewHit < 0) firstNewHit = tick;
        }
        Assert.Equal(30, firstNewHit);
    }

    [Fact]
    public void NativeListRemoveCompactionCanExceedPeriodBound()
    {
        var ships = new List<string> { "protected-A", "remove-B", "target", "protected-C" };
        int cursor = 0;
        void Pass()
        {
            var removals = Scan(ref cursor, 2, ships.Count, true).Select(index => ships[index])
                .Where(ship => ship is "remove-B" or "target").ToArray();
            foreach (string ship in removals) Assert.True(ships.Remove(ship)); // native remove-after-scan compaction
        }
        Pass(); Pass();
        Assert.Contains("target", ships); // The P-tick proof requires stable membership, not just bounded Count.
        Pass();
        Assert.DoesNotContain("target", ships);
        Assert.Equal(new[] { "protected-A", "protected-C" }, ships);
    }

    [Fact]
    public void InstalledIlChangesExactlyOneCallOperandAndRetainsEveryPredicate()
    {
        var original = PatchProcessor.GetOriginalInstructions(CullCadence.Target);
        var patched = CullCadence.Transpiler(original.Select(i => new CodeInstruction(i))).ToList();
        Assert.Equal(original.Count, patched.Count);
        int changed = 0;
        for (int i = 0; i < original.Count; i++)
        {
            Assert.Equal(original[i].opcode, patched[i].opcode);
            Assert.Equal(original[i].labels, patched[i].labels);
            Assert.Equal(original[i].blocks, patched[i].blocks);
            if (Equals(original[i].operand, patched[i].operand)) continue;
            changed++;
            Assert.Equal(CullCadence.NativeMax, original[i].operand);
            Assert.Equal(AccessTools.Method(typeof(CullCadence), nameof(CullCadence.Bound)), patched[i].operand);
            Assert.Equal("get_Count", ((MethodInfo)original[i - 1].operand).Name);
        }
        Assert.Equal(1, changed);
        foreach (string predicate in new[] { "ExistsInSim", "AllowCullShip", "get_IsHumanPlayerShip", "ConstructionEqualsRebuild", "get_HasAnyQueued", "Remove" })
            Assert.Equal(original.Count(i => i.operand is MethodBase m && m.Name == predicate), patched.Count(i => i.operand is MethodBase m && m.Name == predicate));
        original.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => CullCadence.Transpiler(original).ToList());
        Assert.Equal(CullCadence.Requested ? 1 : 0, CullCadence.Patches.Length);
    }

    [Fact]
    public async Task ActualNativeOwnedSpScopesWeakBindingAndExceptionFallback()
    {
        const string marker = "UNOFFICIAL_PATCH_CULL_CADENCE_TEST_CHILD";
        if (Environment.GetEnvironmentVariable(marker) != "1")
        {
            string executable = Path.ChangeExtension(typeof(CullCadenceTests).Assembly.Location, ".exe");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-method"); start.ArgumentList.Add(typeof(CullCadenceTests).FullName + "." + nameof(ActualNativeOwnedSpScopesWeakBindingAndExceptionFallback));
            start.ArgumentList.Add("-noLogo"); start.ArgumentList.Add("-noColor");
            start.Environment[marker] = "1"; start.Environment["UNOFFICIAL_PATCH_CULL_CADENCE"] = "1";
            await RunChild(start);
            return;
        }
        RunNativeCases();
    }

    [Fact]
    public async Task ForeignTargetPatchRefusesActivation()
    {
        const string marker = "UNOFFICIAL_PATCH_CULL_CADENCE_FOREIGN_CHILD";
        if (Environment.GetEnvironmentVariable(marker) != "1")
        {
            var start = new ProcessStartInfo(Path.ChangeExtension(typeof(CullCadenceTests).Assembly.Location, ".exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-method"); start.ArgumentList.Add(typeof(CullCadenceTests).FullName + "." + nameof(ForeignTargetPatchRefusesActivation));
            start.ArgumentList.Add("-noLogo"); start.ArgumentList.Add("-noColor");
            start.Environment[marker] = "1"; start.Environment["UNOFFICIAL_PATCH_CULL_CADENCE"] = "1";
            await RunChild(start);
            return;
        }
        App.Director = Shell<Director>();
        var own = new Harmony(Patcher.HarmonyId); var foreign = new Harmony("performance.tests.cull-foreign");
        try
        {
            own.Patch(CullCadence.Target, prefix: new HarmonyMethod(typeof(CullCadence), nameof(CullCadence.Prefix)),
                transpiler: new HarmonyMethod(typeof(CullCadence), nameof(CullCadence.Transpiler)),
                finalizer: new HarmonyMethod(typeof(CullCadence), nameof(CullCadence.Finalizer)));
            foreign.Patch(CullCadence.Target, prefix: new HarmonyMethod(typeof(CullCadenceTests), nameof(ForeignPrefix)));
            Assert.False(CullCadence.Initialize());
            Assert.False(CullCadence.Ready);
            Assert.Contains("Foreign patch", CullCadence.FailureReason);
            Assert.Equal(1206, CullCadence.Bound(30, 1206));
        }
        finally { CullCadence.Disable(); foreign.UnpatchAll(foreign.Id); own.UnpatchAll(Patcher.HarmonyId); }
    }

    private static void ForeignPrefix() { }

    private static async Task RunChild(ProcessStartInfo start)
    {
        using Process child = Process.Start(start)!;
        Task<string> stdout = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(30_000);
        try { await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false); Assert.Equal(0, child.ExitCode); }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            Console.WriteLine(await stdout.ConfigureAwait(false)); Console.WriteLine(await stderr.ConfigureAwait(false));
        }
    }

    private static void RunNativeCases()
    {
        App.Director = Shell<Director>();
        var harmony = new Harmony(Patcher.HarmonyId);
        var uninitialized = Shell<SimShipsManager>();
        Exception vanilla = Assert.Throws<TargetInvocationException>(() => CullCadence.Target.Invoke(uninitialized, [null, null])).InnerException!;
        try
        {
            harmony.Patch(CullCadence.Target, prefix: new HarmonyMethod(typeof(CullCadence), nameof(CullCadence.Prefix)),
                transpiler: new HarmonyMethod(typeof(CullCadence), nameof(CullCadence.Transpiler)),
                finalizer: new HarmonyMethod(typeof(CullCadence), nameof(CullCadence.Finalizer)));
            Assert.True(CullCadence.Initialize(), CullCadence.FailureReason);
            long nativeCalls = CullCadence.OwnedUpdateCalls;
            Assert.Equal(1206, CullCadence.Bound(30, 1206)); // Ready is not an owned-simulation authorization.
            Assert.Equal(nativeCalls, CullCadence.OwnedUpdateCalls);
            WeakReference<SimRoot> weak = BindEphemeralOwner();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.False(weak.TryGetTarget(out _));
            CullCadence.Prefix(uninitialized, out bool expired); Assert.Equal(1206, CullCadence.Bound(30, 1206)); CullCadence.Finalizer(expired, null);
            SimRoot owner = MakeSpSim();
            Assert.True(CullCadence.BindOwnedSimulation(owner), CullCadence.FailureReason); // expired weak owner may be replaced
            SimShipsManager manager = MakeManager(owner), other = MakeManager(MakeSpSim());
            Assert.True(CullCadence.BindOwnedSimulation(owner)); // same live owner is idempotent
            Assert.False(CullCadence.BindOwnedSimulation(other.Sim)); // a different live owner cannot replace it
            Assert.True(CullCadence.Ready);
            Assert.Throws<TargetInvocationException>(() => CullCadence.Target.Invoke(manager, [null, null]));
            Assert.True(CullCadence.Ready);
            Assert.Equal(1206, CullCadence.Bound(30, 1206)); // actual owned native exception closed the scope
            CullCadence.Prefix(manager, out bool outer);
            Assert.Equal(30, CullCadence.Bound(30, 1206));
            Assert.Equal(nativeCalls + 1, CullCadence.OwnedUpdateCalls);
            Assert.Equal(30, CullCadence.SelectBound(true, 30, 1206));
            Assert.Equal(1206, CullCadence.Bound(0, 1206));
            Assert.Equal(nativeCalls + 1, CullCadence.OwnedUpdateCalls);
            CullCadence.Prefix(other, out bool nested);
            Assert.Equal(1206, CullCadence.Bound(30, 1206));
            Assert.Equal(nativeCalls + 1, CullCadence.OwnedUpdateCalls);
            Assert.Null(CullCadence.Finalizer(nested, null));
            Assert.Equal(30, CullCadence.Bound(30, 1206));
            Assert.Equal(nativeCalls + 2, CullCadence.OwnedUpdateCalls);
            var expected = new InvalidOperationException("native-scope-error");
            Assert.Same(expected, CullCadence.Finalizer(outer, expected));
            Assert.Equal(1206, CullCadence.Bound(30, 1206));
            Exception patched = Assert.Throws<TargetInvocationException>(() => CullCadence.Target.Invoke(uninitialized, [null, null])).InnerException!;
            Assert.Equal(vanilla.GetType(), patched.GetType());
            Assert.Equal(1206, CullCadence.Bound(30, 1206));
            Assert.True(CullCadence.BindOwnedSimulation(owner));
            Assert.Equal(nativeCalls + 2, CullCadence.OwnedUpdateCalls);
            owner.Game!.NetManager = Shell<MPHostManager>();
            CullCadence.Prefix(manager, out bool becameMp); Assert.Equal(1206, CullCadence.Bound(30, 1206)); CullCadence.Finalizer(becameMp, null);
            Assert.False(CullCadence.BindOwnedSimulation(owner));
            Assert.Equal(1206, CullCadence.Bound(30, 1206));
            Assert.Equal(nativeCalls + 2, CullCadence.OwnedUpdateCalls);
        }
        finally { CullCadence.Disable(); harmony.UnpatchAll(Patcher.HarmonyId); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<SimRoot> BindEphemeralOwner()
    {
        SimRoot owner = MakeSpSim();
        Assert.True(CullCadence.BindOwnedSimulation(owner));
        return new WeakReference<SimRoot>(owner);
    }
    private static SimRoot MakeSpSim()
    {
        SimRoot sim = Shell<SimRoot>(); var game = Shell<GameRoot>(); game.NetManager = Shell<SPManager>(); sim.Game = game; return sim;
    }
    private static SimShipsManager MakeManager(SimRoot sim)
    {
        var manager = Shell<SimShipsManager>();
        AccessTools.Field(typeof(SimComponent), "<Sim>k__BackingField").SetValue(manager, sim); return manager;
    }
    private static T Shell<T>() where T : class
    {
        // Every fixture type currently inherits only Object.Finalize; refuse future finalizable native shells.
        Assert.Equal(typeof(object), typeof(T).GetMethod("Finalize", BindingFlags.NonPublic | BindingFlags.Instance)?.DeclaringType);
        return (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    }
    private static int[] Scan(ref int cursor, int period, int count, bool enabled)
    {
        cursor = Mathx.Wrap(cursor + 1, 0, CullCadence.SelectBound(enabled, period, count) - 1);
        var result = new List<int>(); for (int i = cursor; i < count; i += period) result.Add(i); return result.ToArray();
    }
}
