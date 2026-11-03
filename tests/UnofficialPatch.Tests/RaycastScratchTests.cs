using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Cosmoteer;
using Halfling;
using Halfling.Application;
using Halfling.Physics2D.Collision;
using Halfling.Physics2D.Dynamics;
using HarmonyLib;
using Xunit;
using V = Halfling.Geometry.Vector2;

namespace UnofficialPatch.Tests;

public class RaycastScratchTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1", true)]
    public void ExplicitOptInOnly(string? value, bool expected) => Assert.Equal(expected, RaycastScratch.IsRequested(value));

    [Fact]
    public void InstalledBodiesAreUnchangedAndChangedIlIsRejected()
    {
        Assert.Equal(8, RaycastScratch.Targets.Length);
        foreach ((MethodBase target, _) in RaycastScratch.Targets)
        {
            var original = PatchProcessor.GetOriginalInstructions(target);
            var patched = RaycastScratch.Transpiler(original, target).ToArray();
            Assert.Equal(original.Count, patched.Length);
            for (int i = 0; i < original.Count; i++)
            {
                Assert.Equal(original[i].opcode, patched[i].opcode);
                Assert.Equal(original[i].operand, patched[i].operand);
            }
            Assert.Throws<InvalidOperationException>(() => RaycastScratch.Transpiler(original.Skip(1), target).ToArray());
        }
    }

    [Theory]
    [InlineData("native")]
    [InlineData("late")]
    [InlineData("foreign")]
    [InlineData("foreign-later")]
    [InlineData("missing-later")]
    [InlineData("missing")]
    public async Task IsolatedNativeCases(string scenario)
    {
        string? childCase = Environment.GetEnvironmentVariable("RAYCAST_SCRATCH_TEST_CASE");
        if (childCase != null)
        {
            if (childCase == scenario) RunNativeCase(scenario);
            return;
        }
        var start = new ProcessStartInfo(Path.ChangeExtension(typeof(RaycastScratchTests).Assembly.Location, ".exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-method"); start.ArgumentList.Add("UnofficialPatch.Tests.RaycastScratchTests.IsolatedNativeCases");
        start.ArgumentList.Add("-noLogo"); start.ArgumentList.Add("-noColor");
        start.Environment["UNOFFICIAL_PATCH_RAYCAST_SCRATCH"] = "1";
        start.Environment["RAYCAST_SCRATCH_TEST_CASE"] = scenario;
        // The child runs this theory's other rows too; they must not recursively spawn more children.
        await RunChild(start);
    }

    private static async Task RunChild(ProcessStartInfo start)
    {
        using Process child = Process.Start(start)!;
        Task<string> stdout = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(30_000);
        try
        {
            await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            Assert.True(child.ExitCode == 0, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            Console.WriteLine(await stdout.ConfigureAwait(false)); Console.WriteLine(await stderr.ConfigureAwait(false));
        }
    }

    private static void RunNativeCase(string scenario)
    {
        App.Director = (Director)RuntimeHelpers.GetUninitializedObject(typeof(Director));
        if (scenario == "late") App.Director.FrameCount = 1;
        var own = new Harmony(Patcher.HarmonyId);
        var foreign = new Harmony("UnofficialPatch.Tests.RaycastScratch.Foreign");
        try
        {
            if (scenario != "missing")
                foreach ((MethodBase target, _) in RaycastScratch.Targets)
                    own.Patch(target, prefix: new HarmonyMethod(typeof(RaycastScratch), nameof(RaycastScratch.Prefix)),
                        transpiler: new HarmonyMethod(typeof(RaycastScratch), nameof(RaycastScratch.Transpiler)),
                        finalizer: new HarmonyMethod(typeof(RaycastScratch), nameof(RaycastScratch.Finalizer)));
            MethodInfo callback = typeof(PhysicsExtensions).GetNestedTypes(BindingFlags.NonPublic)
                .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                .First(method => method.Name.Contains("<RayCastFirst>", StringComparison.Ordinal));
            if (scenario == "foreign") foreign.Patch(callback, prefix: new HarmonyMethod(typeof(RaycastScratchTests), nameof(ForeignPrefix)));
            if (scenario is not ("native" or "foreign-later" or "missing-later"))
            {
                Assert.False(RaycastScratch.Initialize()); Assert.False(RaycastScratch.Ready);
                Assert.Contains(scenario == "late" ? "FrameCount" : scenario == "foreign" ? "Foreign patch" : "coverage", RaycastScratch.FailureReason);
                return;
            }
            Assert.True(RaycastScratch.Initialize(), RaycastScratch.FailureReason);
            if (scenario == "missing-later")
            {
                own.Unpatch(RaycastScratch.Targets[0].Target, HarmonyPatchType.Prefix, own.Id);
                RaycastScratch.CheckCompatibility(); Assert.False(RaycastScratch.Ready);
                Assert.Contains("coverage", RaycastScratch.FailureReason);
                return;
            }
            if (scenario == "foreign-later")
            {
                foreign.Patch(callback, prefix: new HarmonyMethod(typeof(RaycastScratchTests), nameof(ForeignPrefix)));
                RaycastScratch.CheckCompatibility(); Assert.False(RaycastScratch.Ready);
                Assert.Contains("Foreign patch", RaycastScratch.FailureReason);
                return;
            }
            using var worlds = new Worlds();
            AssertAllEntries(worlds);
            AssertRetention(worlds);
            AssertCrossWorldNesting(worlds);
            AssertExceptionCleanup(worlds);
            AssertThreadIsolation(worlds);
        }
        finally { RaycastScratch.Disable(); foreign.UnpatchAll(foreign.Id); own.UnpatchAll(own.Id); Clear(); }
    }

    private static void ForeignPrefix() { }

    private static void AssertAllEntries(Worlds worlds)
    {
        Body body = worlds.Body;
        Action[] calls =
        [
            () => Assert.Same(body.FixtureList[0], PhysicsExtensions.TestPointFirst(worlds.Occupied, default)),
            () => Assert.True(PhysicsExtensions.RayCast(worlds.Occupied, new V(-2, 0), new V(4, 0))),
            () => Assert.NotNull(PhysicsExtensions.RayCastFirst(worlds.Occupied, new V(-2, 0), new V(4, 0), out _, out _)),
            () => Assert.NotNull(PhysicsExtensions.RayCastFirst(worlds.Occupied, new V(-2, 0), new V(4, 0), out _, out _, null).Item1),
            () => { var hits = new Dictionary<Fixture, (V, V)>(); PhysicsExtensions.RayCastAll(worlds.Occupied, new V(-2, 0), new V(4, 0), hits); Assert.Equal(2, hits.Count); },
            () => Assert.NotNull(PhysicsExtensions.RayCastAny(worlds.Occupied, new V(-2, 0), new V(4, 0), out _, out _)),
            () => Assert.True(PhysicsExtensions.RayCast(body, new V(-2, 0), new V(4, 0))),
            () => Assert.Same(body.FixtureList[0], PhysicsExtensions.RayCastFirst(body, new V(-2, 0), new V(4, 0), out _, out _)),
        ];
        foreach (Action call in calls)
        {
            Seed(body.FixtureList[0]); call(); AssertNeutral();
        }
        Seed(body.FixtureList[0]);
        var miss = PhysicsExtensions.RayCastFirst(worlds.Empty, new V(-2, 0), new V(4, 0), out V point, out V normal, null);
        Assert.Null(miss.Item1); Assert.Null(miss.Item2); Assert.Equal(default, point); Assert.Equal(default, normal); AssertNeutral();
    }

    private static void AssertRetention(Worlds worlds)
    {
        WeakReference capture = AttachCapture(worlds.Empty), data = AttachCapture(worlds.Occupied);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(Alive(capture)); Assert.False(Alive(data)); AssertNeutral();
        object payload = new();
        var hit = PhysicsExtensions.RayCastFirst(worlds.Occupied, new V(-2, 0), new V(4, 0), out _, out _, (_, _) => (true, payload));
        Assert.NotNull(hit.Item1); Assert.Same(payload, hit.Item2);
        var miss = PhysicsExtensions.RayCastFirst(worlds.Empty, new V(-2, 0), new V(4, 0), out _, out _, null);
        Assert.Null(miss.Item1); Assert.Null(miss.Item2); AssertNeutral();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachCapture(World world)
    {
        object payload = new byte[1_048_576];
        _ = PhysicsExtensions.RayCastFirst(world, new V(-2, 0), new V(4, 0), out _, out _, (_, _) => (true, payload));
        return new WeakReference(payload);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static bool Alive(WeakReference weak) => weak.IsAlive;

    private static void AssertCrossWorldNesting(Worlds worlds)
    {
        int boolCalls = 0, dataCalls = 0;
        Assert.Null(PhysicsExtensions.RayCastFirst(worlds.Occupied, new V(-2, 0), new V(4, 0), out _, out _, condition: (_, _) =>
        {
            boolCalls++;
            Assert.Null(PhysicsExtensions.RayCastFirst(worlds.Empty, new V(-2, 0), new V(4, 0), out _, out _, null).Item1);
            return false;
        }));
        Assert.Equal(2, boolCalls); AssertNeutral();
        object data = new();
        var hit = PhysicsExtensions.RayCastFirst(worlds.Occupied, new V(-2, 0), new V(4, 0), out _, out _, (_, _) =>
        {
            dataCalls++;
            Assert.Null(PhysicsExtensions.RayCastFirst(worlds.Empty, new V(-2, 0), new V(4, 0), out _, out _));
            return (true, data);
        });
        Assert.True(dataCalls > 0); Assert.NotNull(hit.Item1); Assert.Same(data, hit.Item2); AssertNeutral();
        var hits = new Dictionary<Fixture, (V, V)>();
        PhysicsExtensions.RayCastAll(worlds.Occupied, new V(-2, 0), new V(4, 0), hits, condition: (_, _) =>
        {
            PhysicsExtensions.RayCastAll(worlds.Empty, new V(-2, 0), new V(4, 0), new Dictionary<Fixture, (V, V)>());
            return true;
        });
        Assert.Equal(2, hits.Count); AssertNeutral();
        // Same-world/same-tree nesting remains unsupported: native reusable traversal stacks are untouched.
    }

    private static void AssertExceptionCleanup(Worlds worlds)
    {
        var expected = new InvalidOperationException("native-callback");
        Exception actual = Assert.Throws<InvalidOperationException>(() => PhysicsExtensions.RayCastFirst(worlds.Occupied, new V(-2, 0), new V(4, 0), out _, out _,
            (_, _) => throw expected));
        Assert.Same(expected, actual); AssertNeutral();
        Assert.Throws<ArgumentNullException>(() => PhysicsExtensions.RayCastAll(worlds.Occupied, new V(-2, 0), new V(4, 0), null!)); AssertNeutral();
    }

    private static void AssertThreadIsolation(Worlds worlds)
    {
        // Each worker owns a distinct native World/tree; only the ThreadStatic helper is shared.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var local = new Worlds(); Seed(local.Body.FixtureList[0]);
                Assert.False(PhysicsExtensions.RayCast(local.Empty, new V(-2, 0), new V(4, 0))); AssertNeutral();
            }
            catch (Exception ex) { failure = ex; }
        });
        Seed(worlds.Body.FixtureList[0]); object? seed = PhysicsExtensions.s_hitData;
        thread.Start(); Assert.True(thread.Join(10_000)); Assert.Null(failure);
        Assert.Same(seed, PhysicsExtensions.s_hitData); Clear();
        object data = new();
        var hit = PhysicsExtensions.RayCastFirst(worlds.Occupied, new V(-2, 0), new V(4, 0), out _, out _, (_, _) =>
        {
            var activeCondition = PhysicsExtensions.s_dataCondition;
            RaycastScratch.Disable();
            var inner = PhysicsExtensions.RayCastFirst(worlds.Empty, new V(-2, 0), new V(4, 0), out _, out _, null);
            Assert.Null(inner.Item1); Assert.Null(inner.Item2);
            Assert.Same(activeCondition, PhysicsExtensions.s_dataCondition);
            return (true, data);
        });
        Assert.NotNull(hit.Item1); Assert.Same(data, hit.Item2); AssertNeutral();
    }

    private static void Seed(Fixture fixture)
    {
        PhysicsExtensions.s_condition = (_, _) => false; PhysicsExtensions.s_dataCondition = (_, _) => (false, new object());
        PhysicsExtensions.s_hitFixture = fixture; PhysicsExtensions.s_hitData = new object();
        PhysicsExtensions.s_hitPoint = new V(12, 34); PhysicsExtensions.s_hitNormal = new V(56, 78);
        PhysicsExtensions.s_hits = new Dictionary<Fixture, (V, V)>();
    }
    private static void AssertNeutral()
    {
        Assert.Null(PhysicsExtensions.s_condition); Assert.Null(PhysicsExtensions.s_dataCondition); Assert.Null(PhysicsExtensions.s_hitFixture);
        Assert.Null(PhysicsExtensions.s_hitData); Assert.Null(PhysicsExtensions.s_hits);
        Assert.Equal(default, PhysicsExtensions.s_hitPoint); Assert.Equal(default, PhysicsExtensions.s_hitNormal);
    }
    private static void Clear()
    {
        PhysicsExtensions.s_condition = null; PhysicsExtensions.s_dataCondition = null; PhysicsExtensions.s_hitFixture = null; PhysicsExtensions.s_hitData = null;
        PhysicsExtensions.s_hits = null; PhysicsExtensions.s_hitPoint = default; PhysicsExtensions.s_hitNormal = default;
    }
    private sealed class Worlds : IDisposable
    {
        internal World Empty { get; } = new(new DynamicTreeBroadPhase<BodyFixtureProxy>());
        internal World Occupied { get; } = new(new DynamicTreeBroadPhase<BodyFixtureProxy>());
        internal Body Body { get; }
        internal Worlds()
        {
            Body = Occupied.CreateBody();
            Body.UseWholeBodyBroadPhase = true; // Native Body ray helpers require its optional fixture tree.
            Body.CreateCircle(0.5f, 1f);
            Occupied.CreateCircle(0.5f, 1f, new System.Numerics.Vector2(2, 0)); Occupied.ProcessChanges();
        }
        public void Dispose() { Empty.Clear(); Occupied.Clear(); }
    }
}
