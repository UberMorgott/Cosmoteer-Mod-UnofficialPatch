using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Halfling;
using Halfling.Application;
using Halfling.Performance;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

public class ParallelIdleParkingTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    public void OnByDefaultWithZeroKillSwitch(string? value, bool expected) => Assert.Equal(expected, ParallelIdleParking.IsRequested(value));

    [Fact]
    public void InstalledBodiesPinnedAndOnlyOuterSpinRewritten()
    {
        Assert.Equal(4, ParallelIdleParking.Targets.Length);
        foreach (var (target, _) in ParallelIdleParking.Targets)
        {
            var original = PatchProcessor.GetOriginalInstructions(target);
            Assert.Equal(original.Count, ParallelIdleParking.RequireIl(target, original).Count);
            original.Insert(0, new CodeInstruction(OpCodes.Nop));
            Assert.Throws<InvalidOperationException>(() => ParallelIdleParking.RequireIl(target, original));
        }
        var native = PatchProcessor.GetOriginalInstructions(ParallelIdleParking.RunThreadTarget);
        var generator = new DynamicMethod("idle-test", typeof(void), Type.EmptyTypes).GetILGenerator();
        var patched = ParallelIdleParking.WorkerHook.Transpiler(native, generator).ToList();
        Assert.Single(patched, i => i.Calls(AccessTools.Method(typeof(ParallelIdleParking), nameof(ParallelIdleParking.CaptureEpoch))));
        Assert.Single(patched, i => i.Calls(AccessTools.Method(typeof(ParallelIdleParking), nameof(ParallelIdleParking.Wait))));
        Assert.DoesNotContain(patched, i => i.Calls(AccessTools.Method(typeof(SpinWait), nameof(SpinWait.SpinOnce), [typeof(int)])));
        int scan = patched.FindIndex(i => i.operand is MethodInfo { Name: "RunUntilEmpty" });
        int epoch = patched.FindIndex(i => i.operand is MethodInfo { Name: nameof(ParallelIdleParking.CaptureEpoch) });
        Assert.Equal(epoch + 4, scan); // capture/store precede the two native argument loads on every loop entry.
        Assert.NotEmpty(patched[epoch].labels);
        Assert.Equal(native.Count + 3, patched.Count);
    }

    [Fact]
    public void PublicationBetweenScanAndWaitNeverParks()
    {
        var gate = new ParallelIdleParking.IdleGate(static () => true, 0, 30_000);
        gate.Enable();
        long epoch = gate.ReadEpoch();
        gate.Signal();
        var spin = new SpinWait();
        gate.WaitForPublication(ref spin, -1, epoch);
        Assert.Equal(0, gate.ParkCount);
        Assert.Equal(1, gate.SignalCount);
    }

    [Fact]
    public void SignalAndDisableWakeAllWithoutWaitingForSafetyTimeout()
    {
        var gate = new ParallelIdleParking.IdleGate(static () => true, 0, 30_000);
        gate.Enable();
        Thread[] sleepers = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            var spin = new SpinWait();
            gate.WaitForPublication(ref spin, -1, gate.ReadEpoch());
        })
        { IsBackground = true }).ToArray();
        try
        {
            foreach (Thread thread in sleepers) thread.Start();
            Assert.True(SpinWait.SpinUntil(() => gate.ParkCount == 4, 3000));
            gate.Signal();
            foreach (Thread thread in sleepers) Assert.True(thread.Join(2000));
            var disabledSleeper = new Thread(() =>
            {
                var spin = new SpinWait();
                gate.WaitForPublication(ref spin, -1, gate.ReadEpoch());
            })
            { IsBackground = true };
            disabledSleeper.Start();
            Assert.True(SpinWait.SpinUntil(() => gate.ParkCount == 5, 3000));
            gate.Disarm();
            Assert.True(disabledSleeper.Join(2000));
        }
        finally
        {
            gate.Disarm();
            foreach (Thread thread in sleepers) Assert.True(thread.Join(2000));
        }
    }

    [Fact]
    public void NativeWarmupAndDisabledFallbackKeepSpinSemantics()
    {
        var gate = new ParallelIdleParking.IdleGate(static () => true, 3, 30_000);
        gate.Enable();
        var spin = new SpinWait();
        for (int i = 0; i < 3; i++) gate.WaitForPublication(ref spin, -1, gate.ReadEpoch());
        Assert.Equal(3, spin.Count);
        Assert.Equal(0, gate.ParkCount);
        gate.Disarm();
        gate.WaitForPublication(ref spin, -1, gate.ReadEpoch());
        Assert.Equal(4, spin.Count);
        var stopped = new ParallelIdleParking.IdleGate(static () => false, 0, 30_000);
        stopped.Enable();
        stopped.WaitForPublication(ref spin, -1, stopped.ReadEpoch());
        Assert.Equal(4, spin.Count);
        Assert.Equal(0, stopped.ParkCount);
    }

    [Fact]
    public async Task NativeSchedulerIsolatedRegression()
    {
        if (await RunInChild(nameof(NativeSchedulerIsolatedRegression))) return;
        RunNativeScheduler();
    }

    [Fact]
    public async Task NativeStartupRejectsNonzeroFrameCount()
    {
        if (await RunInChild(nameof(NativeStartupRejectsNonzeroFrameCount))) return;
        App.Director = (Director)RuntimeHelpers.GetUninitializedObject(typeof(Director));
        App.Director.FrameCount = 1;
        Assert.False(ParallelIdleParking.Initialize());
        Assert.False(ParallelIdleParking.Ready);
        Assert.Contains("FrameCount", ParallelIdleParking.FailureReason);
        Assert.False(FastParallel.s_createdThreads);
    }

    [Fact]
    public async Task NativeStartupRejectsAlreadyCreatedWorkers()
    {
        if (await RunInChild(nameof(NativeStartupRejectsAlreadyCreatedWorkers))) return;
        App.Director = (Director)RuntimeHelpers.GetUninitializedObject(typeof(Director));
        FastParallel.ThreadCount = 1;
        FastParallel.Start();
        FastParallel.Stop();
        Assert.False(ParallelIdleParking.Initialize());
        Assert.False(ParallelIdleParking.Ready);
        Assert.Contains("already created", ParallelIdleParking.FailureReason);
    }

    private static async Task<bool> RunInChild(string methodName)
    {
        // Child process I/O needs no synchronization context; each Fact resumes on its captured xUnit context.
        const string marker = "PERFORMANCE_IDLE_PARK_NATIVE_CHILD";
        if (Environment.GetEnvironmentVariable(marker) != methodName)
        {
            string executable = Path.ChangeExtension(typeof(ParallelIdleParkingTests).Assembly.Location, ".exe");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-method"); start.ArgumentList.Add(typeof(ParallelIdleParkingTests).FullName + "." + methodName);
            start.ArgumentList.Add("-noLogo"); start.ArgumentList.Add("-noColor");
            start.Environment[marker] = methodName;
            start.Environment["PERFORMANCE_IDLE_PARK"] = "1";
            using Process child = Process.Start(start)!;
            Task<string> stdout = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            Task<string> stderr = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(30_000);
            try
            {
                await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                Assert.Equal(0, child.ExitCode);
            }
            finally
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                Console.WriteLine(await stdout.ConfigureAwait(false)); Console.WriteLine(await stderr.ConfigureAwait(false));
            }
            return true;
        }
        return false;
    }

    private static void RunNativeScheduler()
    {
        Assert.True(ParallelIdleParking.Requested);
        Assert.False(FastParallel.s_createdThreads);
        FastParallel.ThreadCount = 3;
        App.Director = (Director)RuntimeHelpers.GetUninitializedObject(typeof(Director));
        var harmony = new Harmony(Patcher.HarmonyId);
        try
        {
            foreach ((MethodBase target, Type patch) in ParallelIdleParking.Patches)
                harmony.Patch(target, transpiler: new HarmonyMethod(patch, "Transpiler"),
                    finalizer: patch == typeof(ParallelIdleParking.WorkerHook) ? null : new HarmonyMethod(patch, "Finalizer"));
            Assert.True(ParallelIdleParking.Initialize(), ParallelIdleParking.FailureReason);
            VerifyNativeQueueResizeAndRestoration();
            FastParallel.Start();
            Assert.True(SpinWait.SpinUntil(() => ParallelIdleParking.ParkCalls >= 3, 3000));
            Assert.Throws<InvalidOperationException>(FastParallel.Start);
            for (int round = 0; round < 100; round++)
            {
                int total = 0;
                FastParallel.For(0, 96, (start, end, _) => { for (int i = start; i < end; i++) Interlocked.Add(ref total, i); }, batchSize: 1);
                Assert.Equal(4560, total);
            }
            int nested = 0, workerPublished = 0, mainThread = Environment.CurrentManagedThreadId;
            FastParallel.For(0, 32, (start, end, _) =>
            {
                if (Environment.CurrentManagedThreadId != mainThread) Interlocked.Increment(ref workerPublished);
                for (int i = start; i < end; i++)
                    FastParallel.For(0, 17, (first, last, _) => Interlocked.Add(ref nested, last - first), batchSize: 1);
            }, batchSize: 1);
            Assert.Equal(32 * 17, nested);
            Assert.True(workerPublished > 0);
            var expected = new InvalidOperationException("native-body-error");
            int visited = 0;
            AggregateException aggregate = Assert.Throws<AggregateException>(() =>
                FastParallel.For(0, 32, (first, last, _) =>
                {
                    Interlocked.Add(ref visited, last - first);
                    if (first <= 7 && last > 7) throw expected;
                }, batchSize: 1));
            Assert.Contains(expected, aggregate.InnerExceptions);
            Assert.Equal(32, visited);
            FastParallel.Stop();
            Assert.Throws<InvalidOperationException>(FastParallel.Stop);
            FastParallel.Start();
            int resumed = 0;
            FastParallel.For(0, 31, (first, last, _) => Interlocked.Add(ref resumed, last - first), batchSize: 1);
            Assert.Equal(31, resumed);
            Assert.True(SpinWait.SpinUntil(() => ParallelIdleParking.ParkCalls >= 6, 3000));
            var bench = new Harmony(BeamFrozen.BenchPhaseTimingOwner);
            try
            {
                Type timing = typeof(Bench.FramePhaseTiming);
                bench.Patch(AccessTools.Method(typeof(FastParallel), nameof(FastParallel.For)),
                    prefix: new HarmonyMethod(timing, "ForPrefix"), finalizer: new HarmonyMethod(timing, "ForFinalizer"));
                bench.Patch(AccessTools.GetDeclaredMethods(typeof(FastParallel)).Single(m => m.Name.Contains("g___WaitUntilFinished|", StringComparison.Ordinal)),
                    prefix: new HarmonyMethod(timing, "WaitPrefix"), finalizer: new HarmonyMethod(timing, "WaitFinalizer"));
                ParallelIdleParking.CheckCompatibility(); // exact bench phase-timing hooks are accepted; others are not
                Assert.True(ParallelIdleParking.Ready, ParallelIdleParking.FailureReason);
                FastParallel.For(0, 31, (first, last, _) => Interlocked.Add(ref resumed, last - first), batchSize: 1);
                Assert.Equal(62, resumed);
            }
            finally { bench.UnpatchAll(bench.Id); }
            var foreign = new Harmony("performance.tests.idle-foreign");
            try
            {
                foreign.Patch(AccessTools.PropertyGetter(typeof(FastParallel), nameof(FastParallel.IsRunning)),
                    prefix: new HarmonyMethod(typeof(ParallelIdleParkingTests), nameof(ForeignPrefix)));
                ParallelIdleParking.CheckCompatibility();
                Assert.Contains("Foreign patch", ParallelIdleParking.FailureReason);
            }
            finally { foreign.UnpatchAll(foreign.Id); }
            Assert.False(ParallelIdleParking.Ready);
            FastParallel.For(0, 29, (first, last, _) => Interlocked.Add(ref resumed, last - first), batchSize: 1);
            Assert.Equal(91, resumed);
            FastParallel.Stop();
            Assert.False(ParallelIdleParking.Initialize()); // already-created forever workers cannot be opted back in.
        }
        finally
        {
            ParallelIdleParking.Disable();
            if (FastParallel.IsRunning) FastParallel.Stop();
            harmony.UnpatchAll(Patcher.HarmonyId);
        }
    }

    private static void ForeignPrefix() { }

    [Fact]
    public void DependencyGuardIncludesClosedPoolDefinitionsAndNestedNativeMethods()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        Type pool = FastParallel.s_liveTasks.GetType().GetGenericTypeDefinition().MakeGenericType(typeof(object));
        foreach (Type root in new[] { typeof(FastParallel), pool })
            foreach (Type type in new[] { root }.Concat(root.GetNestedTypes(flags)))
                foreach (MethodInfo method in type.GetMethods(flags)) Assert.True(ParallelIdleParking.IsDependency(method), method.ToString());
        Assert.False(ParallelIdleParking.IsDependency(AccessTools.Method(typeof(object), nameof(ReferenceEquals))));
    }

    private static void VerifyNativeQueueResizeAndRestoration()
    {
        // Before Start no worker can consume these native task objects. Exercise actual resizing and _PutBack publication.
        var previousPool = FastParallel.s_liveTasks;
        var crumb = FastParallel.GetCrumb();
        var tasks = Enumerable.Range(0, 12).Select(_ => new FastParallel.ParallelTask { RetainCount = 1 }).ToArray();
        foreach (var task in tasks) FastParallel.AddToLive(task, crumb);
        Assert.NotSame(previousPool, FastParallel.s_liveTasks);
        Assert.Equal(12, FastParallel.s_liveTasks.Count);
        long before = ParallelIdleParking.WakeSignals;
        FastParallel.RemoveFromLive(tasks[5], crumb);
        Assert.Equal(11, FastParallel.s_liveTasks.Count);
        Assert.True(ParallelIdleParking.WakeSignals > before); // displaced tasks really passed through hooked AddToLive.
        foreach (var task in tasks.Where((_, index) => index != 5)) FastParallel.RemoveFromLive(task, crumb);
        Assert.Equal(0, FastParallel.s_liveTasks.Count);
    }

    [Fact]
    public void FinalizerPreservesExceptionAndPublishesEvenOnFailure()
    {
        long before = ParallelIdleParking.WakeSignals;
        var expected = new InvalidOperationException("publication-error");
        Assert.Same(expected, ParallelIdleParking.SignalHook.Finalizer(expected));
        Assert.Null(ParallelIdleParking.SignalHook.Finalizer(null));
        Assert.Equal(before + 2, ParallelIdleParking.WakeSignals);
    }

    [Fact]
    public void SafetyTimeoutRescansWithoutNotification()
    {
        var gate = new ParallelIdleParking.IdleGate(static () => true, 0, 1);
        gate.Enable();
        var spin = new SpinWait();
        gate.WaitForPublication(ref spin, -1, gate.ReadEpoch());
        Assert.Equal(1, gate.ParkCount);
        Assert.Equal(0, gate.SignalCount);
    }
}
