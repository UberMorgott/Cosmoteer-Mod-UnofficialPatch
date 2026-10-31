using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cosmoteer.Ships.Rendering;
using HarmonyLib;
using Halfling.Graphics;
using Xunit;
using ThreadState = System.Threading.ThreadState;
using Vector3 = Halfling.Geometry.Vector3;

namespace Performance.Tests;

public sealed class AtlasWriteCensusTests
{
    [Theory]
    [InlineData("3", "1", true)]
    [InlineData("2", "1", false)]
    [InlineData("3", null, false)]
    [InlineData("3", "true", false)]
    public void ExplicitModeThreeOptInOnly(string? mode, string? flag, bool expected) => Assert.Equal(expected, AtlasWriteCensus.IsRequested(mode, flag));
    [Fact]
    public void ComparisonPreservesSignedZeroAndNanPayloadBits()
    {
        AtlasQuad a = default, b = default;
        Assert.Equal(AtlasWriteCensus.Difference.None, AtlasWriteCensus.Compare(a, b));
        b.V1.Location = new Vector3(BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), 0, 0);
        Assert.Equal(AtlasWriteCensus.Difference.Other, AtlasWriteCensus.Compare(a, b));
        a.V1.Location = b.V1.Location = new Vector3(BitConverter.Int32BitsToSingle(0x7FC00001), 0, 0);
        Assert.Equal(AtlasWriteCensus.Difference.None, AtlasWriteCensus.Compare(a, b));
        b.V1.Location = new Vector3(BitConverter.Int32BitsToSingle(0x7FC00002), 0, 0);
        Assert.Equal(AtlasWriteCensus.Difference.Other, AtlasWriteCensus.Compare(a, b));
    }
    [Fact]
    public void FieldComparisonCoversEveryByteOfAllFourVertices()
    {
        Assert.True(AtlasWriteCensus.FieldCensusAvailable);
        AtlasQuad baseline = default;
        Assert.Equal(AtlasWriteCensus.Difference.None, AtlasWriteCensus.Compare(baseline, baseline));
        for (int offset = 0; offset < 464; offset++)
        {
            AtlasQuad changed = default;
            Unsafe.Add(ref Unsafe.As<AtlasQuad, byte>(ref changed), offset) = 1;
            var expected = offset % 116 is >= 40 and < 56 ? AtlasWriteCensus.Difference.Color : AtlasWriteCensus.Difference.Other;
            Assert.Equal(expected, AtlasWriteCensus.Compare(baseline, changed));
        }
        AtlasQuad mixed = default;
        Unsafe.Add(ref Unsafe.As<AtlasQuad, byte>(ref mixed), 40) = 1;
        Unsafe.Add(ref Unsafe.As<AtlasQuad, byte>(ref mixed), 463) = 1;
        Assert.Equal(AtlasWriteCensus.Difference.Both, AtlasWriteCensus.Compare(baseline, mixed));
    }
    [Theory]
    [InlineData(0, (int)AtlasWriteCensus.Difference.Other)]
    [InlineData(40, (int)AtlasWriteCensus.Difference.Color)]
    public void FieldComparisonRetainsSignedZeroAndNanPayload(int offset, int expected)
    {
        AtlasQuad first = default, second = default;
        WriteBits(ref second, offset, unchecked((int)0x80000000));
        Assert.Equal((AtlasWriteCensus.Difference)expected, AtlasWriteCensus.Compare(first, second));
        WriteBits(ref first, offset, 0x7FC00001); WriteBits(ref second, offset, 0x7FC00001);
        Assert.Equal(AtlasWriteCensus.Difference.None, AtlasWriteCensus.Compare(first, second));
        WriteBits(ref second, offset, 0x7FC00002);
        Assert.Equal((AtlasWriteCensus.Difference)expected, AtlasWriteCensus.Compare(first, second));
    }
    [Fact]
    public void FieldComparisonDoesNotAllocateAfterWarmup()
    {
        AtlasQuad first = default, second = default;
        WriteBits(ref second, 40, 1); WriteBits(ref second, 56, 1);
        int checksum = 0;
        for (int i = 0; i < 10_000; i++) checksum += (int)AtlasWriteCensus.Compare(first, second)!.Value;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) checksum += (int)AtlasWriteCensus.Compare(first, second)!.Value;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated); Assert.Equal(60_000, checksum);
    }
    private static void WriteBits(ref AtlasQuad quad, int offset, int bits) =>
        MemoryMarshal.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref quad, 1)).Slice(offset), in bits);
    [Fact]
    public void SingleComparisonResultPartitionsCountersAndUnknownErrors()
    {
        long writes = AtlasWriteCensus.Writes, identical = AtlasWriteCensus.Identical, different = AtlasWriteCensus.Different;
        long unknown = AtlasWriteCensus.Unknown, errors = AtlasWriteCensus.Errors;
        long color = AtlasWriteCensus.ColorOnly, other = AtlasWriteCensus.OtherOnly, both = AtlasWriteCensus.ColorAndOther;
        var interval = new AtlasWriteCensus.Interval(); interval.CloseSuccessful();
        AtlasWriteCensus.Record(interval, AtlasWriteCensus.Difference.None, false);
        AtlasWriteCensus.Record(interval, AtlasWriteCensus.Difference.Color, false);
        AtlasWriteCensus.Record(interval, AtlasWriteCensus.Difference.Other, false);
        AtlasWriteCensus.Record(interval, AtlasWriteCensus.Difference.Both, false);
        AtlasWriteCensus.Record(interval, null, false); AtlasWriteCensus.Record(interval, null, true);
        Assert.Equal(writes + 6, AtlasWriteCensus.Writes); Assert.Equal(identical + 1, AtlasWriteCensus.Identical);
        Assert.Equal(different + 3, AtlasWriteCensus.Different); Assert.Equal(unknown + 2, AtlasWriteCensus.Unknown); Assert.Equal(errors + 1, AtlasWriteCensus.Errors);
        Assert.Equal(color + 1, AtlasWriteCensus.ColorOnly); Assert.Equal(other + 1, AtlasWriteCensus.OtherOnly); Assert.Equal(both + 1, AtlasWriteCensus.ColorAndOther);
        Assert.Equal(AtlasWriteCensus.Different, AtlasWriteCensus.ColorOnly + AtlasWriteCensus.OtherOnly + AtlasWriteCensus.ColorAndOther);
        Assert.Equal(AtlasWriteCensus.Writes, AtlasWriteCensus.Identical + AtlasWriteCensus.Different + AtlasWriteCensus.Unknown);
        Assert.Equal(AtlasWriteCensus.IntervalKind.Unknown, interval.CloseSuccessful());
    }
    [Fact]
    public void IntervalPriorityIsUnknownThenDifferentThenIdenticalThenEmpty()
    {
        var interval = new AtlasWriteCensus.Interval();
        interval.Observe(AtlasWriteCensus.Difference.None); Assert.Equal(AtlasWriteCensus.IntervalKind.Unknown, interval.CloseSuccessful());
        Assert.Equal(AtlasWriteCensus.IntervalKind.Empty, interval.CloseSuccessful());
        interval.Observe(AtlasWriteCensus.Difference.None); interval.Observe(AtlasWriteCensus.Difference.None); Assert.Equal(AtlasWriteCensus.IntervalKind.Identical, interval.CloseSuccessful());
        interval.Observe(AtlasWriteCensus.Difference.None); interval.Observe(AtlasWriteCensus.Difference.Color); Assert.Equal(AtlasWriteCensus.IntervalKind.Different, interval.CloseSuccessful());
        interval.Observe(AtlasWriteCensus.Difference.Both); interval.Observe(null); Assert.Equal(AtlasWriteCensus.IntervalKind.Unknown, interval.CloseSuccessful());
        interval.Invalidate(); Assert.Equal(AtlasWriteCensus.IntervalKind.Unknown, interval.CloseSuccessful());
    }
    [Fact]
    public async Task ActualNativeWritesRemainUnchangedAndWorkerCountsAreAtomic()
    {
        if (Environment.GetEnvironmentVariable("ATLAS_WRITE_CENSUS_CHILD") == "1") { NativeCase(); return; }
        var start = new ProcessStartInfo(Path.ChangeExtension(typeof(AtlasWriteCensusTests).Assembly.Location, ".exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-method"); start.ArgumentList.Add("Performance.Tests.AtlasWriteCensusTests.ActualNativeWritesRemainUnchangedAndWorkerCountsAreAtomic");
        start.ArgumentList.Add("-noLogo"); start.Environment["ATLAS_WRITE_CENSUS_CHILD"] = "1";
        start.Environment["PERFORMANCE_ATLAS_STATIC"] = "3"; start.Environment["PERFORMANCE_ATLAS_WRITE_CENSUS"] = "1";
        using Process child = Process.Start(start)!;
        Task<string> stdout = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken), stderr = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); timeout.CancelAfter(20_000);
        try { await child.WaitForExitAsync(timeout.Token).ConfigureAwait(true); Assert.True(child.ExitCode == 0, await stdout.ConfigureAwait(true) + await stderr.ConfigureAwait(true)); }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(true); } }
    }
    private static void NativeCase()
    {
        Assert.True(AtlasPartialUploads.WriteCensusRequested);
        using var hooks = new AtlasPartialUploadsTests();
        using var first = new AtlasPartialUploadsTests.NativeFixture();
        using var second = new AtlasPartialUploadsTests.NativeFixture();
        using var firstBinding = new ShadowBinding(first.Array);
        using var secondBinding = new ShadowBinding(second.Array);
        ManagedAtlasQuad a = first.Manager.CreateQuad(default, 0), b = second.Manager.CreateQuad(default, 0);
        var state = AtlasPartialUploads.StateOf(first.Array)!; var other = AtlasPartialUploads.StateOf(second.Array)!;
        long unknownIntervals = AtlasPartialUploads.CensusUnknownIntervals;
        Assert.True(state.Begin().Finish(true)); Assert.True(other.Begin().Finish(true));
        Assert.Equal(unknownIntervals + 2, AtlasPartialUploads.CensusUnknownIntervals);
        first.SetDirty(false); second.SetDirty(false);
        long identical = AtlasPartialUploads.CensusIdenticalWrites;
        uint changes = first.Manager.ChangeCount;
        a.Data = default;
        Assert.Equal(changes + 1, first.Manager.ChangeCount); Assert.True(first.IsDirty); Assert.True(state.Dirty.HasPendingChanges);
        Assert.Equal(identical + 1, AtlasPartialUploads.CensusIdenticalWrites);
        Assert.True(state.Begin().Finish(true)); first.SetDirty(false);
        long different = AtlasPartialUploads.CensusDifferentWrites;
        long otherWrites = AtlasPartialUploads.CensusOtherOnlyWrites;
        AtlasQuad changed = default; changed.V1.Location = new Vector3(1, 2, 3); a.Data = changed;
        Assert.Equal(changed.V1.Location, first.Array[0].V1.Location); Assert.Equal(different + 1, AtlasPartialUploads.CensusDifferentWrites);
        Assert.Equal(otherWrites + 1, AtlasPartialUploads.CensusOtherOnlyWrites);
        Assert.True(state.Begin().Finish(true)); first.SetDirty(false);
        long colorWrites = AtlasPartialUploads.CensusColorOnlyWrites;
        WriteBits(ref changed, 40, 1); a.Data = changed;
        Assert.Equal(colorWrites + 1, AtlasPartialUploads.CensusColorOnlyWrites);
        Assert.True(state.Begin().Finish(true)); first.SetDirty(false);
        long unknown = AtlasPartialUploads.CensusUnknownWrites;
        FieldInfo index = AccessTools.Field(AtlasWriteCoverage.InternalQuadType, "_index"); index.SetValue(a, 1000);
        try { Assert.Equal("i", Assert.Throws<ArgumentOutOfRangeException>(() => a.Data = default).ParamName); }
        finally { index.SetValue(a, 0); }
        Assert.Equal(unknown + 1, AtlasPartialUploads.CensusUnknownWrites);
        Assert.Equal(changed.V1.Location, first.Array[0].V1.Location);
        a.Data = default; Assert.True(state.Begin().Finish(true)); first.SetDirty(false);
        var mutation = (AtlasPartialUploads.Mutation)AccessTools.Method(typeof(AtlasPartialUploads), "BeginMutation")
            .Invoke(null, [first.Manager, -1, false, AtlasPartialUploads.ScopeKind.Write])!;
        try
        {
            IntPtr originalSource = state.Source; state.Source = IntPtr.Add(originalSource, 1);
            uint unchanged = first.Manager.ChangeCount;
            long unknownBefore = AtlasPartialUploads.CensusUnknownWrites;
            AtlasPartialUploads.ObserveWrite(a, state.Owner, in changed);
            Assert.Equal(unknownBefore + 1, AtlasPartialUploads.CensusUnknownWrites);
            Assert.Equal(IntPtr.Add(originalSource, 1), state.Source); // Observer must not reconcile or Bind.
            Assert.Equal(unchanged, first.Manager.ChangeCount); Assert.False(first.IsDirty);
            state.Source = originalSource;
            long errors = AtlasPartialUploads.CensusObserverErrors;
            AtlasPartialUploads.ObserveWrite(null!, state.Owner, in changed);
            Assert.Equal(errors + 1, AtlasPartialUploads.CensusObserverErrors); // Unexpected observer error is contained.
            var arrays = (ConditionalWeakTable<GraphicsArray<AtlasQuad>, AtlasPartialUploads.UploadState>)AccessTools.Field(typeof(AtlasPartialUploads), "Arrays").GetValue(null)!;
            Assert.True(arrays.Remove(first.Array));
            AtlasPartialUploads.ObserveWrite(a, state.Owner, in changed);
            Assert.Null(AtlasPartialUploads.StateOf(first.Array)); Assert.False(first.IsDirty);
            Assert.Equal(unchanged, first.Manager.ChangeCount); // Missing state remains missing until original Mark runs.
        }
        finally { AtlasPartialUploads.DataWriteHook.Finalizer(null, mutation); }
        a.Data = default;
        state = AtlasPartialUploads.StateOf(first.Array)!;
        Assert.True(state.Begin().Finish(true)); first.SetDirty(false);
        long writes = AtlasPartialUploads.CensusWrites; identical = AtlasPartialUploads.CensusIdenticalWrites;
        Exception? errorA = null, errorB = null;
        var workerA = new Thread(() => { try { for (int i = 0; i < 100; i++) a.Data = default; } catch (Exception error) { errorA = error; } });
        var workerB = new Thread(() => { try { for (int i = 0; i < 100; i++) b.Data = default; } catch (Exception error) { errorB = error; } });
        try { workerA.Start(); workerB.Start(); Assert.True(workerA.Join(5000)); Assert.True(workerB.Join(5000)); }
        finally
        {
            if (!workerA.ThreadState.HasFlag(ThreadState.Unstarted)) Assert.True(workerA.Join(5000));
            if (!workerB.ThreadState.HasFlag(ThreadState.Unstarted)) Assert.True(workerB.Join(5000));
        }
        Assert.Null(errorA); Assert.Null(errorB);
        Assert.Equal(writes + 200, AtlasPartialUploads.CensusWrites); Assert.Equal(identical + 200, AtlasPartialUploads.CensusIdenticalWrites);
        long closed = AtlasPartialUploads.CensusSuccessfulIntervals;
        state.Fail(); Assert.Equal(closed, AtlasPartialUploads.CensusSuccessfulIntervals);
        Assert.True(state.Begin().Finish(true)); first.SetDirty(false);
        for (int i = 0; i < 1000; i++) a.Data = default;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) a.Data = default;
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated);
        var foreignScope = (AtlasPartialUploads.Mutation)AccessTools.Method(typeof(AtlasPartialUploads), "BeginMutation")
            .Invoke(null, [second.Manager, -1, false, AtlasPartialUploads.ScopeKind.Write])!;
        FieldInfo arrayField = AccessTools.Field(typeof(GraphicsList<AtlasQuad>), "_array");
        var secondList = second.Manager._lists[0]!;
        long wrongOwnerUnknown = AtlasPartialUploads.CensusUnknownWrites;
        try
        {
            arrayField.SetValue(secondList, first.Array);
            AtlasPartialUploads.ObserveWrite(b, other.Owner, in changed);
            Assert.Equal(wrongOwnerUnknown + 1, AtlasPartialUploads.CensusUnknownWrites);
            Assert.Equal(AtlasWriteCensus.IntervalKind.Identical, other.WriteInterval!.CloseSuccessful());
        }
        finally
        {
            arrayField.SetValue(secondList, second.Array);
            AtlasPartialUploads.DataWriteHook.Finalizer(null, foreignScope);
        }
        state.Owner.Gate.Enter();
        try { Assert.Equal(AtlasWriteCensus.IntervalKind.Identical, state.WriteInterval!.CloseSuccessful()); }
        finally { state.Owner.Gate.Exit(); }
        Assert.Equal(AtlasPartialUploads.CensusDifferentWrites, AtlasPartialUploads.CensusColorOnlyWrites + AtlasPartialUploads.CensusOtherOnlyWrites + AtlasPartialUploads.CensusColorAndOtherWrites);
    }

    // Identity-only headless binding: no COM/GPU method is called on this shell, including during cleanup.
    private sealed class ShadowBinding : IDisposable
    {
        private readonly GraphicsArray<AtlasQuad> _array;
        private readonly FieldInfo _vertices, _pointer;
        private readonly SharpDX.Direct3D11.Buffer _buffer;
        [SuppressMessage("Usage", "CA1816", Justification = "The uninitialized COM shell inherits DisposeBase's native-release finalizer. Suppress it before assigning the test sentinel pointer; cleanup restores zero without COM calls.")]
        internal unsafe ShadowBinding(GraphicsArray<AtlasQuad> array)
        {
            _array = array;
            _vertices = AccessTools.Field(array.GetType(), "_vertices");
            _pointer = AccessTools.Field(typeof(SharpDX.CppObject), "_nativePointer");
            _buffer = (SharpDX.Direct3D11.Buffer)RuntimeHelpers.GetUninitializedObject(typeof(SharpDX.Direct3D11.Buffer));
            GC.SuppressFinalize(_buffer); // The real COM finalizer must never dereference this test-only sentinel.
            _pointer.SetValue(_buffer, Pointer.Box((void*)1, _pointer.FieldType));
            _vertices.SetValue(_array, _buffer);
        }
        public unsafe void Dispose()
        {
            _vertices.SetValue(_array, null);
            _pointer.SetValue(_buffer, Pointer.Box((void*)0, _pointer.FieldType));
        }
    }
}
