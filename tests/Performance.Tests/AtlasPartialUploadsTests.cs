using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmoteer.Ships.Rendering;
using Halfling.Graphics;
using HarmonyLib;
using SharpDX;
using SharpDX.Direct3D11;
using Xunit;
using Vector3 = Halfling.Geometry.Vector3;

namespace Performance.Tests;

public sealed class AtlasPartialUploadsTests : IDisposable
{
    private const string HarmonyId = "performance.tests.atlas-partial-uploads";
    private readonly Harmony _harmony = new(HarmonyId);
    private static readonly InvalidOperationException ApiFailure = new("native core sentinel");
    private static int _coreCalls;
    private static Action<AtlasQuadManager>? _drawExit;

    [Theory]
    [InlineData(null, false)]
    [InlineData("1", false)]
    [InlineData("2", true)]
    [InlineData("3", true)]
    [InlineData("4", false)]
    public void TrackerRecognizesOnlyPartialModes(string? requested, bool expected) =>
        Assert.Equal(expected, AtlasPartialUploads.IsRequested(requested));

    public AtlasPartialUploadsTests()
    {
        Assembly backend = Assembly.LoadFrom(System.IO.Path.Combine(GameAssemblies.GameBin, "HalflingPlatformWDX.dll"));
        AtlasPartialUploads.ConfigureBackend(backend);
        AccessTools.Field(typeof(AtlasPartialUploads), "_thread").SetValue(null, Environment.CurrentManagedThreadId);
        AccessTools.Property(typeof(AtlasPartialUploads), "Ready").SetValue(null, true);
        foreach ((MethodBase target, Type patch) in AtlasPartialUploads.WriterPatches.Concat(AtlasPartialUploads.DrawPatches))
            _harmony.Patch(target,
                prefix: new HarmonyMethod(AccessTools.Method(patch, "Prefix")),
                transpiler: new HarmonyMethod(AccessTools.Method(patch, "Transpiler")),
                finalizer: AccessTools.Method(patch, "Finalizer") is { } finalizer ? new HarmonyMethod(finalizer) : null);
    }

    public void Dispose()
    {
        AtlasPartialUploads.Disable();
        _harmony.UnpatchAll(HarmonyId);
        _drawExit = null;
    }

    [Theory]
    [InlineData(false, 0x0600012F, 36)]
    [InlineData(true, 0x06000160, 95)]
    public void ExactNativeUploadMethodsArePinned(bool core, int token, int bytes)
    {
        MethodInfo method = core ? AtlasPartialUploads.UploadCore : AtlasPartialUploads.UploadTarget;
        Assert.Equal(token, method.MetadataToken);
        Assert.Equal(bytes, method.GetMethodBody()!.GetILAsByteArray()!.Length);
        var code = PatchProcessor.GetOriginalInstructions(method);
        Assert.Equal(code.Count, AtlasPartialUploads.RequireUploadIl(method, code).Count);
        code.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => AtlasPartialUploads.RequireUploadIl(method, code));
    }

    [Fact]
    public void SourceValidationRejectsUnknownPointersSubresourcesAndBufferKinds()
    {
        const int capacity = 64;
        int bytes = capacity * Unsafe.SizeOf<AtlasQuad>();
        var source = new DataBox(new IntPtr(0x123400), bytes, bytes);
        var description = new BufferDescription { SizeInBytes = bytes, Usage = ResourceUsage.Default, BindFlags = BindFlags.VertexBuffer };
        Assert.True(AtlasPartialUploads.MatchesSource(source, source.DataPointer, capacity, description, 0));
        Assert.False(AtlasPartialUploads.MatchesSource(source, IntPtr.Add(source.DataPointer, 1), capacity, description, 0));
        Assert.False(AtlasPartialUploads.MatchesSource(source, source.DataPointer, capacity, description, 1));
        Assert.False(AtlasPartialUploads.MatchesSource(source, source.DataPointer, capacity + 1, description, 0));
        source.RowPitch--;
        Assert.False(AtlasPartialUploads.MatchesSource(source, source.DataPointer, capacity, description, 0));
        source.RowPitch++;
        description.Usage = ResourceUsage.Dynamic;
        Assert.False(AtlasPartialUploads.MatchesSource(source, source.DataPointer, capacity, description, 0));
        description.Usage = ResourceUsage.Default;
        description.CpuAccessFlags = CpuAccessFlags.Write;
        Assert.False(AtlasPartialUploads.MatchesSource(source, source.DataPointer, capacity, description, 0));
    }

    [Fact]
    public void UnknownNativeResourceUsesOriginalWrapperAndPreservesCoreException()
    {
        var context = (DeviceContext)NativeShell(typeof(DeviceContext));
        var buffer = (SharpDX.Direct3D11.Buffer)NativeShell(typeof(SharpDX.Direct3D11.Buffer));
        _coreCalls = 0;
        _harmony.Patch(AtlasPartialUploads.UploadCore, prefix: new HarmonyMethod(typeof(AtlasPartialUploadsTests), nameof(ThrowCoreFailure)));
        _harmony.Patch(AtlasPartialUploads.UploadTarget,
            prefix: new HarmonyMethod(typeof(AtlasPartialUploads.UploadHook), "Prefix"),
            transpiler: new HarmonyMethod(typeof(AtlasPartialUploads.UploadHook), "Transpiler"),
            finalizer: new HarmonyMethod(typeof(AtlasPartialUploads.UploadHook), "Finalizer"));
        foreach ((MethodBase target, Type patch) in AtlasPartialUploads.WriterPatches.Concat(AtlasPartialUploads.DrawPatches).Append((AtlasPartialUploads.UploadTarget, typeof(AtlasPartialUploads.UploadHook))))
        {
            Patches installed = Assert.IsType<Patches>(Harmony.GetPatchInfo(target));
            Assert.Contains(installed.Prefixes, item => item.owner == HarmonyId);
            Assert.Contains(installed.Transpilers, item => item.owner == HarmonyId);
            if (AccessTools.Method(patch, "Finalizer") != null) Assert.Contains(installed.Finalizers, item => item.owner == HarmonyId);
        }
        var call = AtlasPartialUploads.UploadTarget.CreateDelegate<Action<DeviceContext, DataBox, Resource, int>>();
        Assert.Same(ApiFailure, Assert.Throws<InvalidOperationException>(() => call(context, new DataBox(new IntPtr(123), 456, 789), buffer, 0)));
        Assert.Equal(1, _coreCalls);
    }

    private static void ThrowCoreFailure()
    {
        _coreCalls++;
        throw ApiFailure;
    }

    [SuppressMessage("Usage", "CA1816", Justification = "Uninitialized native test shells own no constructor-created COM lifetime; fixtures initialize and free their own CPU memory explicitly.")]
    private static object NativeShell(Type type)
    {
        object value = RuntimeHelpers.GetUninitializedObject(type);
        GC.SuppressFinalize(value);
        return value;
    }

    [Fact]
    public void ForeignDependencyAtFrameBoundaryDisablesAndRestoresPendingNativeDirty()
    {
        using var fixture = new NativeFixture();
        ManagedAtlasQuad quad = fixture.Manager.CreateQuad(default, 0);
        var state = AtlasPartialUploads.StateOf(fixture.Array)!;
        Assert.True(state.Begin().Finish(true));
        fixture.SetDirty(false);
        quad.Data = Quad(1);
        fixture.SetDirty(false);
        // Test hooks have a foreign owner; the production compatibility check must fail closed on them.
        AccessTools.Method(typeof(AtlasPartialUploads), "OnFrameStarting").Invoke(null, [null, EventArgs.Empty]);
        Assert.False(AtlasPartialUploads.Ready);
        Assert.True(state.Dirty.RequiresFullUpload);
        Assert.True(fixture.IsDirty);
        Assert.NotNull(AtlasPartialUploads.FailureReason);
    }

    [Fact]
    public void OffThreadUploadRejectionOnlyDisarmsUntilGraphicsBoundary()
    {
        using var fixture = new NativeFixture();
        ManagedAtlasQuad quad = fixture.Manager.CreateQuad(default, 0);
        var state = AtlasPartialUploads.StateOf(fixture.Array)!;
        Assert.True(state.Begin().Finish(true));
        fixture.SetDirty(false);
        quad.Data = Quad(1);
        AtlasPartialUploads.UploadAttempt attempt = state.Begin();
        long version = state.Dirty.Version;
        int count = state.Dirty.DirtyCount;
        Exception? failure = null;
        var context = (DeviceContext)NativeShell(typeof(DeviceContext));
        var buffer = (SharpDX.Direct3D11.Buffer)NativeShell(typeof(SharpDX.Direct3D11.Buffer));
        var worker = new Thread(() =>
        {
            try { AtlasPartialUploads.UploadHook.Prefix(context, default, buffer, 0, out _); }
            catch (Exception ex) { failure = ex; }
        });
        worker.Start(); worker.Join();
        Assert.Null(failure);
        Assert.False(AtlasPartialUploads.Ready);
        Assert.Equal(version, state.Dirty.Version);
        Assert.Equal(count, state.Dirty.DirtyCount);
        Assert.False(state.NeedsRecovery);
        fixture.SetDirty(false);
        AccessTools.Method(typeof(AtlasPartialUploads), "OnFrameStarting").Invoke(null, [null, EventArgs.Empty]);
        Assert.True(state.Dirty.RequiresFullUpload);
        Assert.True(fixture.IsDirty);
        Assert.False(attempt.Finish(true));
    }

    [Fact]
    public void PhysicsWorkersCanCreateAndWriteIndependentOwners()
    {
        using var first = new NativeFixture();
        using var second = new NativeFixture();
        using var start = new ManualResetEventSlim();
        Exception? firstError = null, secondError = null;
        Thread Make(NativeFixture fixture, Action<Exception> fail) => new(() =>
        {
            try
            {
                start.Wait();
                for (int i = 0; i < 32; i++) fixture.Manager.CreateQuad(Quad(i), 0).Data = Quad(i + 100);
            }
            catch (Exception ex) { fail(ex); }
        });
        Thread a = Make(first, ex => firstError = ex), b = Make(second, ex => secondError = ex);
        a.Start(); b.Start(); start.Set();
        Assert.True(a.Join(5000)); Assert.True(b.Join(5000));
        Assert.Null(firstError); Assert.Null(secondError);
        Assert.True(AtlasPartialUploads.Ready);
        Assert.Equal(32, first.Manager.Count); Assert.Equal(32, second.Manager.Count);
        for (int i = 0; i < 32; i++)
        {
            Assert.Equal(Quad(i + 100).V1.Location, first.Array[i].V1.Location);
            Assert.Equal(Quad(i + 100).V1.Location, second.Array[i].V1.Location);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [SuppressMessage("ReSharper", "AccessToDisposedClosure", Justification = "The synchronous draw callback is cleared and every started worker is joined before the captured fixtures/events leave their using scopes.")]
    public void WriterWaitsThroughNativeDrawAndDirtyClearEvenAfterShutdown(int drawIndex, bool shutdown)
    {
        using var fixture = new NativeFixture();
        ManagedAtlasQuad quad = fixture.Manager.CreateQuad(default, 0);
        using var requested = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        Exception? failure = null;
        var writer = new Thread(() =>
        {
            try { requested.Set(); quad.Data = Quad(42); }
            catch (Exception ex) { failure = ex; }
            finally { finished.Set(); }
        });
        MethodInfo draw = AtlasWriteCoverage.DrawTargets[drawIndex].Target;
        _drawExit = _ =>
        {
            fixture.SetDirty(false); // Emulates the last native dirtyclear before leaving the full draw scope.
            writer.Start();
            Assert.True(requested.Wait(5000));
            Assert.True(SpinWait.SpinUntil(() => (writer.ThreadState & ThreadState.WaitSleepJoin) != ThreadState.Running || finished.IsSet, 5000));
            Assert.False(finished.IsSet);
            if (shutdown) AtlasPartialUploads.Disable();
            Assert.False(finished.Wait(30));
        };
        _harmony.Patch(draw, postfix: new HarmonyMethod(typeof(AtlasPartialUploadsTests), nameof(DrawExit)));
        try { draw.Invoke(fixture.Manager, [null]); }
        finally { _drawExit = null; JoinStarted(writer); }
        Assert.True(writer.Join(5000));
        Assert.Null(failure);
        Assert.True(fixture.IsDirty); // Writer ran after dirtyclear; no update was erased by the draw's final assignment.
        Assert.Equal(Quad(42).V1.Location, fixture.Array[0].V1.Location);
    }

    private static void DrawExit(AtlasQuadManager __instance) => _drawExit?.Invoke(__instance);
    private static void JoinStarted(Thread thread)
    {
        if (!thread.ThreadState.HasFlag(ThreadState.Unstarted)) Assert.True(thread.Join(5000));
    }

    [Fact]
    [SuppressMessage("ReSharper", "AccessToDisposedClosure", Justification = "Worker execution is synchronous and joined inside the callback; the callback is cleared before fixtures are disposed.")]
    public void DrawExceptionReleasesGateAndOtherOwnerNeverWaitsForIt()
    {
        using var first = new NativeFixture();
        using var second = new NativeFixture();
        MethodInfo draw = AtlasWriteCoverage.DrawTargets[0].Target;
        _drawExit = _ =>
        {
            var worker = new Thread(() => second.Manager.CreateQuad(default, 0));
            worker.Start();
            Assert.True(worker.Join(5000));
            throw ApiFailure;
        };
        _harmony.Patch(draw, postfix: new HarmonyMethod(typeof(AtlasPartialUploadsTests), nameof(DrawExit)));
        try { Assert.Same(ApiFailure, Assert.Throws<TargetInvocationException>(() => draw.Invoke(first.Manager, [null])).InnerException); }
        finally { _drawExit = null; }
        var next = new Thread(() => first.Manager.CreateQuad(default, 0));
        next.Start(); Assert.True(next.Join(5000));
        Assert.Equal(1, first.Manager.Count); Assert.Equal(1, second.Manager.Count);
    }

    [Fact]
    public void NativeSetterExceptionReleasesItsOwnerGate()
    {
        using var fixture = new NativeFixture();
        ManagedAtlasQuad quad = fixture.Manager.CreateQuad(default, 0);
        FieldInfo index = AccessTools.Field(AtlasWriteCoverage.InternalQuadType, "_index");
        index.SetValue(quad, 1000);
        Assert.Equal("i", Assert.Throws<ArgumentOutOfRangeException>(() => quad.Data = default).ParamName);
        index.SetValue(quad, 0);
        Exception? error = null;
        var worker = new Thread(() => { try { quad.Data = Quad(9); } catch (Exception ex) { error = ex; } });
        worker.Start(); Assert.True(worker.Join(5000));
        Assert.Null(error);
        Assert.Equal(Quad(9).V1.Location, fixture.Array[0].V1.Location);
    }

    [Fact]
    [SuppressMessage("ReSharper", "AccessToDisposedClosure", Justification = "The held test gate is released and both started workers are joined in finally before captured fixtures are disposed.")]
    public void RegistryLockIsReleasedBeforeSnapshotWaitsForAnOwner()
    {
        using var first = new NativeFixture();
        using var second = new NativeFixture();
        first.Manager.CreateQuad(default, 0);
        object gate = AtlasPartialUploads.StateOf(first.Array)!.Owner.Gate;
        Exception? recoveryError = null, createError = null;
        var recovery = new Thread(() =>
        {
            try { AccessTools.Method(typeof(AtlasPartialUploads), "Recover").Invoke(null, null); }
            catch (Exception ex) { recoveryError = ex; }
        });
        var create = new Thread(() => { try { second.Manager.CreateQuad(default, 0); } catch (Exception ex) { createError = ex; } });
        Monitor.Enter(gate);
        try
        {
            recovery.Start();
            Assert.True(SpinWait.SpinUntil(() => (recovery.ThreadState & ThreadState.WaitSleepJoin) != ThreadState.Running, 5000));
            create.Start();
            Assert.True(create.Join(5000)); // Registration must progress while snapshot is waiting for the other owner.
        }
        finally { Monitor.Exit(gate); JoinStarted(create); JoinStarted(recovery); }
        Assert.True(recovery.Join(5000));
        Assert.Null(recoveryError); Assert.Null(createError);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [SuppressMessage("ReSharper", "AccessToDisposedClosure", Justification = "The draw gate is released and every started worker is joined in finally before the captured native fixture is disposed.")]
    public void WorkerDrawFailsBeforeWaitingForOwnerEvenAfterShutdown(int drawIndex, bool shutdown)
    {
        using var fixture = new NativeFixture();
        fixture.Manager.CreateQuad(default, 0);
        AtlasPartialUploads.DrawHook.Prefix(fixture.Manager, out AtlasPartialUploads.Mutation scope);
        Exception? error = null;
        Thread? worker = null;
        try
        {
            if (shutdown) AtlasPartialUploads.Disable();
            worker = new Thread(() =>
            {
                try { AtlasWriteCoverage.DrawTargets[drawIndex].Target.Invoke(fixture.Manager, [null]); }
                catch (Exception ex) { error = ex; }
            });
            worker.Start();
            Assert.True(worker.Join(5000)); // Main still owns the gate: entering it on the worker would deadlock here.
            var wrapped = Assert.IsType<TargetInvocationException>(error);
            Assert.IsType<InvalidOperationException>(wrapped.InnerException);
            Assert.Equal(1, scope.Owner!.DrawDepth);
        }
        finally
        {
            AtlasPartialUploads.DrawHook.Finalizer(null, scope);
            if (worker != null) JoinStarted(worker);
        }
    }

    [Fact]
    public void SuccessfulKnownUploadCountersIncludeFullPartialAndRetryBytesOnce()
    {
        using var fixture = new NativeFixture();
        ManagedAtlasQuad quad = fixture.Manager.CreateQuad(default, 0);
        var state = AtlasPartialUploads.StateOf(fixture.Array)!;
        long full = AtlasPartialUploads.FullUploads, partial = AtlasPartialUploads.PartialUploads;
        long uploaded = AtlasPartialUploads.UploadedBytes, equivalent = AtlasPartialUploads.FullEquivalentBytes;
        long calls = AtlasPartialUploads.UploadCalls;
        long capacity = fixture.ByteCount, stride = Unsafe.SizeOf<AtlasQuad>();
        Assert.Null(AtlasPartialUploads.UploadHook.Finalizer(null, state.Begin()));
        fixture.SetDirty(false);
        quad.Data = Quad(1);
        AtlasPartialUploads.UploadAttempt small = state.Begin().RequireCommit().AddSubmitted(stride);
        Assert.Null(AtlasPartialUploads.UploadHook.Finalizer(null, small));
        fixture.SetDirty(false);
        quad.Data = Quad(2);
        AtlasPartialUploads.UploadAttempt retry = state.Begin().RequireCommit().AddSubmitted(stride);
        quad.Data = Quad(3);
        long alreadySubmitted = retry.SubmittedBytes;
        int alreadyCalled = retry.SubmittedCalls;
        AtlasPartialUploads.CloseInvalidated(ref retry);
        retry = state.Begin().RequireCommit().AddSubmitted(alreadySubmitted, alreadyCalled);
        Assert.Null(AtlasPartialUploads.UploadHook.Finalizer(null, retry));
        Assert.Same(ApiFailure, AtlasPartialUploads.UploadHook.Finalizer(ApiFailure, state.Begin().RequireCommit()));
        Assert.Equal(full + 2, AtlasPartialUploads.FullUploads);
        Assert.Equal(partial + 1, AtlasPartialUploads.PartialUploads);
        Assert.Equal(uploaded + capacity * 2 + stride * 2, AtlasPartialUploads.UploadedBytes);
        Assert.Equal(equivalent + capacity * 3, AtlasPartialUploads.FullEquivalentBytes);
        Assert.Equal(calls + 4, AtlasPartialUploads.UploadCalls);
    }

    [Fact]
    public void CoalescedNativeWritesMirrorOneSpanIncludingUnchangedGap()
    {
        using var fixture = new NativeFixture();
        ManagedAtlasQuad[] quads = Enumerable.Range(0, 8).Select(i => fixture.Manager.CreateQuad(Quad(i + 1), 0)).ToArray();
        var state = AtlasPartialUploads.StateOf(fixture.Array)!;
        byte[] gpu = new byte[fixture.ByteCount];
        Apply(fixture, state, gpu, true);
        quads[0].Data = Quad(17); quads[7].Data = Quad(23);
        AtlasPartialUploads.UploadAttempt attempt = state.Begin(singleRange: true).RequireCommit();
        Assert.False(attempt.Plan.Full);
        Assert.Equal(1, attempt.Plan.RangeCount);
        Assert.Equal(new AtlasUploadRange(0, 8), attempt.Plan.GetRange(0));
        CopyRange(fixture, gpu, attempt.Plan.GetRange(0));
        attempt = attempt.AddSubmitted(8L * Unsafe.SizeOf<AtlasQuad>());
        long calls = AtlasPartialUploads.UploadCalls;
        Assert.Null(AtlasPartialUploads.UploadHook.Finalizer(null, attempt));
        fixture.SetDirty(false);
        Assert.Equal(calls + 1, AtlasPartialUploads.UploadCalls);
        AssertMirror(fixture, gpu);
    }

    [Fact]
    public void MultiRegionRetryRetainsEveryUnderlyingCallWithoutWrapperDoubleCount()
    {
        using var fixture = new NativeFixture();
        ManagedAtlasQuad first = fixture.Manager.CreateQuad(default, 0);
        fixture.Manager.CreateQuad(default, 0);
        ManagedAtlasQuad last = fixture.Manager.CreateQuad(default, 0);
        var state = AtlasPartialUploads.StateOf(fixture.Array)!;
        Assert.True(state.Begin().Finish(true)); fixture.SetDirty(false);
        first.Data = Quad(1); last.Data = Quad(2);
        AtlasPartialUploads.UploadAttempt old = state.Begin(singleRange: false).RequireCommit();
        Assert.Equal(2, old.Plan.RangeCount);
        old = old.AddSubmitted(Unsafe.SizeOf<AtlasQuad>()).AddSubmitted(Unsafe.SizeOf<AtlasQuad>());
        long bytes = old.SubmittedBytes; int submittedCalls = old.SubmittedCalls;
        last.Data = Quad(3);
        AtlasPartialUploads.CloseInvalidated(ref old);
        AtlasPartialUploads.UploadAttempt full = state.Begin().RequireCommit().AddSubmitted(bytes, submittedCalls);
        long calls = AtlasPartialUploads.UploadCalls, uploaded = AtlasPartialUploads.UploadedBytes;
        Assert.Null(AtlasPartialUploads.UploadHook.Finalizer(null, full));
        Assert.Equal(calls + 3, AtlasPartialUploads.UploadCalls);
        Assert.Equal(uploaded + fixture.ByteCount + 2L * Unsafe.SizeOf<AtlasQuad>(), AtlasPartialUploads.UploadedBytes);
    }

    [Fact]
    [SuppressMessage("ReSharper", "AccessToDisposedClosure", Justification = "Both nested draw invocations are synchronous and the callback is cleared in finally before its native fixture is disposed.")]
    public void ReentrantDrawAndNativeSetterPreserveOwnerScope()
    {
        using var fixture = new NativeFixture();
        ManagedAtlasQuad quad = fixture.Manager.CreateQuad(default, 0);
        MethodInfo draw = AtlasWriteCoverage.DrawTargets[0].Target;
        int callbacks = 0;
        _drawExit = _ =>
        {
            if (++callbacks != 1) return;
            draw.Invoke(fixture.Manager, [null]);
            quad.Data = Quad(77);
            Assert.Equal(Quad(77).V1.Location, fixture.Array[0].V1.Location);
        };
        _harmony.Patch(draw, postfix: new HarmonyMethod(typeof(AtlasPartialUploadsTests), nameof(DrawExit)));
        try
        {
            draw.Invoke(fixture.Manager, [null]);
            Assert.Equal(2, callbacks);
            Assert.True(AtlasPartialUploads.Ready);
        }
        finally { _drawExit = null; }
    }

    [Fact]
    [SuppressMessage("ReSharper", "AccessToDisposedClosure", Justification = "The start event stays alive until the started worker is joined in finally, before fixtures and the event are disposed.")]
    public void RegistrySnapshotAndCleanupDoNotHoldGlobalLockWhileWaitingForOwners()
    {
        NativeFixture[] fixtures = Enumerable.Range(0, 16).Select(_ => new NativeFixture()).ToArray();
        using var start = new ManualResetEventSlim();
        Exception? error = null;
        var worker = new Thread(() =>
        {
            try { start.Wait(); foreach (NativeFixture fixture in fixtures) fixture.Manager.CreateQuad(default, 0); }
            catch (Exception ex) { error = ex; }
        });
        try
        {
            worker.Start(); start.Set();
            for (int i = 0; i < 30 && worker.IsAlive; i++) AccessTools.Method(typeof(AtlasPartialUploads), "Recover").Invoke(null, null);
            Assert.True(worker.Join(5000));
            Assert.Null(error);
            AtlasPartialUploads.Disable();
            Assert.All(fixtures, fixture => Assert.Equal(1, fixture.Manager.Count));
        }
        finally
        {
            start.Set(); JoinStarted(worker);
            foreach (NativeFixture fixture in fixtures) fixture.Dispose();
        }
    }

    [Fact]
    public void ActualNativeMutationsAndPlansMirrorEveryCapacityByte()
    {
        using var fixture = new NativeFixture();
        ManagedAtlasQuad first = fixture.Manager.CreateQuad(Quad(1), 0);
        fixture.Manager.CreateQuad(Quad(2), 0);
        ManagedAtlasQuad last = fixture.Manager.CreateQuad(Quad(3), 0);
        AtlasPartialUploads.UploadState state = Assert.IsType<AtlasPartialUploads.UploadState>(AtlasPartialUploads.StateOf(fixture.Array));
        byte[] gpu = new byte[fixture.ByteCount];
        Apply(fixture, state, gpu, expectedFull: true);
        first.Data = Quad(7);
        last.Data = Quad(11);
        Assert.Equal(2, state.Dirty.DirtyCount);
        Apply(fixture, state, gpu, expectedFull: false);
        first.Dispose(); // Native swap-removal changes the owner generation and requires full baseline.
        Assert.Equal(Quad(11).V1.Location, fixture.Array[0].V1.Location);
        Apply(fixture, state, gpu, expectedFull: true);
        fixture.Manager.Clear();
        Assert.Equal(0, fixture.Manager.Count);
        Apply(fixture, state, gpu, expectedFull: true);
    }

    [Fact]
    public void FailedNestedAndOverlappingUploadsStayFullAndRecoverNativeDirtyFlag()
    {
        using var fixture = new NativeFixture();
        ManagedAtlasQuad quad = fixture.Manager.CreateQuad(Quad(1), 0);
        var state = AtlasPartialUploads.StateOf(fixture.Array)!;
        byte[] gpu = new byte[fixture.ByteCount];
        Apply(fixture, state, gpu, true);
        quad.Data = Quad(4);
        AtlasPartialUploads.UploadAttempt failed = state.Begin();
        var failure = new InvalidOperationException("native update failed");
        Assert.Same(failure, AtlasPartialUploads.UploadHook.Finalizer(failure, failed));
        Assert.True(state.Dirty.RequiresFullUpload);
        Assert.True(state.NeedsRecovery);
        Apply(fixture, state, gpu, true);

        quad.Data = Quad(6);
        AtlasPartialUploads.UploadAttempt outer = state.Begin();
        Assert.False(outer.Plan.Full);
        quad.Data = Quad(8);
        Assert.False(outer.CanContinue);
        AtlasPartialUploads.UploadAttempt nested = state.Begin();
        Assert.False(nested.OwnsPlan); // Native wrapper must execute its full call; never skip an unowned nested plan.
        Assert.False(nested.Finish(true));
        Assert.False(outer.Finish(true));
        fixture.SetDirty(false); // Emulate the native generic caller clearing after the wrapper returned.
        AccessTools.Method(typeof(AtlasPartialUploads), "Recover").Invoke(null, null);
        Assert.True(fixture.IsDirty);
        Assert.True(state.Dirty.RequiresFullUpload);
        Apply(fixture, state, gpu, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidatedPartialGetsCompleteFullMirrorBeforeNextFrame(bool structural)
    {
        using var fixture = new NativeFixture();
        ManagedAtlasQuad first = fixture.Manager.CreateQuad(Quad(1), 0);
        fixture.Manager.CreateQuad(Quad(2), 0);
        ManagedAtlasQuad last = fixture.Manager.CreateQuad(Quad(3), 0);
        var state = AtlasPartialUploads.StateOf(fixture.Array)!;
        byte[] gpu = new byte[fixture.ByteCount];
        Apply(fixture, state, gpu, true);
        first.Data = Quad(7);
        last.Data = Quad(11);
        AtlasPartialUploads.UploadAttempt partial = state.Begin().RequireCommit();
        Assert.False(partial.Plan.Full);
        CopyRange(fixture, gpu, partial.Plan.GetRange(0));
        if (structural) first.Dispose(); else last.Data = Quad(23);
        Assert.False(partial.CanContinue);
        AtlasPartialUploads.UploadState retryState = AtlasPartialUploads.CloseInvalidated(ref partial);
        Assert.Null(partial.State);
        AtlasPartialUploads.UploadAttempt full = retryState.Begin().RequireCommit();
        Assert.True(full.Plan.Full);
        CopyRange(fixture, gpu, full.Plan.GetRange(0));
        Assert.Null(AtlasPartialUploads.UploadHook.Finalizer(null, full));
        fixture.SetDirty(false);
        Assert.False(state.NeedsRecovery);
        AssertMirror(fixture, gpu); // Correct now, before any FrameStarting recovery could repair it.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedBindingOrShutdownAbortsSuppressedUpload(bool shutdown)
    {
        using var fixture = new NativeFixture();
        ManagedAtlasQuad quad = fixture.Manager.CreateQuad(default, 0);
        var state = AtlasPartialUploads.StateOf(fixture.Array)!;
        Assert.True(state.Begin().Finish(true));
        fixture.SetDirty(false);
        quad.Data = Quad(1);
        AtlasPartialUploads.UploadAttempt attempt = state.Begin().RequireCommit();
        if (shutdown) AtlasPartialUploads.Disable(); else state.Identity++;
        Assert.Throws<InvalidOperationException>(() => AtlasPartialUploads.CloseInvalidated(ref attempt));
        Assert.Null(attempt.State);
        Assert.Equal(0, state.UploadDepth);
        Assert.True(state.Dirty.RequiresFullUpload);
    }

    [Fact]
    public void SuppressedUploadCannotReportSuccessAfterCommitInvalidation()
    {
        using var fixture = new NativeFixture();
        ManagedAtlasQuad quad = fixture.Manager.CreateQuad(default, 0);
        var state = AtlasPartialUploads.StateOf(fixture.Array)!;
        Assert.True(state.Begin().Finish(true));
        fixture.SetDirty(false);
        quad.Data = Quad(1);
        AtlasPartialUploads.UploadAttempt attempt = state.Begin().RequireCommit();
        quad.Data = Quad(2);
        Assert.IsType<InvalidOperationException>(AtlasPartialUploads.UploadHook.Finalizer(null, attempt));
        attempt = state.Begin().RequireCommit();
        quad.Data = Quad(3);
        var original = new InvalidOperationException("original API failure");
        Assert.Same(original, AtlasPartialUploads.UploadHook.Finalizer(original, attempt));
    }

    [Fact]
    public void WarmTrackedWritesAndPlanTransactionsAllocateNothing()
    {
        using var fixture = new NativeFixture();
        ManagedAtlasQuad quad = fixture.Manager.CreateQuad(default, 0);
        var state = AtlasPartialUploads.StateOf(fixture.Array)!;
        Assert.True(state.Begin().Finish(true));
        fixture.SetDirty(false);
        void Run()
        {
            quad.Data = default;
            state.Begin().Finish(true);
            fixture.SetDirty(false);
        }
        for (int i = 0; i < 10_000; i++) Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) Run();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void GlobalTrackingDoesNotRetainNativeOwnersArraysOrState()
    {
        WeakReference[] weak = MakeExpired();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.All(weak, item => Assert.False(item.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] MakeExpired()
    {
        using var fixture = new NativeFixture();
        fixture.Manager.CreateQuad(default, 0);
        return [new(fixture.Manager), new(fixture.Array), new(AtlasPartialUploads.StateOf(fixture.Array))];
    }

    private static AtlasQuad Quad(float value)
    {
        AtlasQuad quad = default;
        quad.V1.Location = new Vector3(value, value + 1, value + 2);
        return quad;
    }

    private static void Apply(NativeFixture fixture, AtlasPartialUploads.UploadState state, byte[] gpu, bool expectedFull)
    {
        AtlasPartialUploads.UploadAttempt attempt = state.Begin();
        Assert.Equal(expectedFull, attempt.Plan.Full);
        for (int i = 0; i < attempt.Plan.RangeCount; i++)
        {
            AtlasUploadRange range = attempt.Plan.GetRange(i);
            CopyRange(fixture, gpu, range);
        }
        Assert.True(attempt.Finish(true));
        fixture.SetDirty(false);
        AssertMirror(fixture, gpu);
    }

    private static unsafe void CopyRange(NativeFixture fixture, byte[] gpu, AtlasUploadRange range)
    {
        int offset = range.Start * Unsafe.SizeOf<AtlasQuad>();
        int bytes = range.Count * Unsafe.SizeOf<AtlasQuad>();
        new ReadOnlySpan<byte>((void*)IntPtr.Add(fixture.Pointer, offset), bytes).CopyTo(gpu.AsSpan(offset, bytes));
    }

    private static unsafe void AssertMirror(NativeFixture fixture, byte[] gpu) =>
        Assert.True(new ReadOnlySpan<byte>((void*)fixture.Pointer, fixture.ByteCount).SequenceEqual(gpu));

    private sealed class NativeFixture : IDisposable
    {
        internal readonly AtlasQuadManager Manager = (AtlasQuadManager)RuntimeHelpers.GetUninitializedObject(typeof(AtlasQuadManager));
        internal readonly GraphicsArray<AtlasQuad> Array;
        internal readonly IntPtr Pointer;
        internal int ByteCount => 64 * Unsafe.SizeOf<AtlasQuad>();
        private readonly Action<object, bool> _setDirty;
        private readonly FieldInfo _dirty;
        internal bool IsDirty => (bool)_dirty.GetValue(Array)!;

        internal unsafe NativeFixture()
        {
            Assembly backend = Assembly.LoadFrom(System.IO.Path.Combine(GameAssemblies.GameBin, "HalflingPlatformWDX.dll"));
            Type type = backend.GetType("Halfling.Graphics.D3D11.D3D11StaticGraphicsArray`1", true)!.MakeGenericType(typeof(AtlasQuad));
            Array = (GraphicsArray<AtlasQuad>)NativeShell(type);
            Pointer = (IntPtr)NativeMemory.AlignedAlloc((nuint)ByteCount, 16);
            new Span<byte>((void*)Pointer, ByteCount).Clear();
            AccessTools.Field(type, "_data").SetValue(Array, System.Reflection.Pointer.Box((void*)Pointer, AccessTools.Field(type, "_data").FieldType));
            AccessTools.Field(type, "_size").SetValue(Array, 64);
            object gfx = NativeShell(backend.GetType("Halfling.Graphics.D3D11.D3D11GraphicsManager", true)!);
            AccessTools.Field(type, "_gfx").SetValue(Array, gfx);
            var list = (GraphicsList<AtlasQuad>)RuntimeHelpers.GetUninitializedObject(typeof(GraphicsList<AtlasQuad>));
            AccessTools.Field(typeof(GraphicsList<AtlasQuad>), "_array").SetValue(list, Array);
            Manager._lists = [list];
            Manager._managedAtlasQuads = [new()];
            _dirty = AccessTools.Field(type, "_dirty");
            var setter = new DynamicMethod("TestNativeDirty", typeof(void), [typeof(object), typeof(bool)], typeof(AtlasPartialUploadsTests).Module, true);
            ILGenerator il = setter.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, type); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, _dirty); il.Emit(OpCodes.Ret);
            _setDirty = setter.CreateDelegate<Action<object, bool>>();
        }
        internal void SetDirty(bool value) => _setDirty(Array, value);
        public void Dispose() => Manager.Dispose();
    }
}
