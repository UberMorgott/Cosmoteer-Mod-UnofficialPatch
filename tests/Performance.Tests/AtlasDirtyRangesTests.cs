using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace Performance.Tests;

public sealed class AtlasDirtyRangesTests
{
    [Fact]
    public void StartsFullThenTracksExactContiguousRangesIncludingWordBoundaryAndLastElement()
    {
        var mirror = new BufferMirror(160);
        Assert.True(mirror.Ranges.RequiresFullUpload);
        mirror.Upload();
        mirror.Write(0, 11);
        mirror.Write(62, 12);
        mirror.Write(63, 13);
        mirror.Write(64, 14);
        mirror.Write(159, 15);
        mirror.Write(64, 16);
        Assert.Equal(5, mirror.Ranges.DirtyCount);
        AtlasUploadPlan plan = mirror.Ranges.Plan();
        Assert.False(plan.Full);
        Assert.Equal(3, plan.RangeCount);
        Assert.Equal(new AtlasUploadRange(0, 1), plan.GetRange(0));
        Assert.Equal(new AtlasUploadRange(62, 3), plan.GetRange(1));
        Assert.Equal(new AtlasUploadRange(159, 1), plan.GetRange(2));
        mirror.Upload(plan);
        Assert.False(mirror.Ranges.HasPendingChanges);
        Assert.Equal(0, mirror.Ranges.DirtyCount);
        Assert.Equal(0, mirror.Ranges.Plan().RangeCount);
    }

    [Theory]
    [InlineData(10, 32, 5, 1)]
    [InlineData(11, 32, 6, 1)]
    [InlineData(100, 2, 3, 3)]
    public void DenseOrOverBudgetWritesFallBackToFull(int capacity, int budget, int count, int spacing)
    {
        var mirror = new BufferMirror(capacity, budget);
        mirror.Upload();
        for (int i = 0; i < count; i++) mirror.Write(i * spacing, (byte)(i + 1));
        AtlasUploadPlan plan = mirror.Ranges.Plan();
        Assert.True(plan.Full);
        Assert.True(mirror.Ranges.RequiresFullUpload);
        Assert.Equal(1, plan.RangeCount);
        Assert.Equal(new AtlasUploadRange(0, capacity), plan.GetRange(0));
        mirror.Upload(plan);
    }

    [Fact]
    public void ExactBudgetAndBelowHalfStayPartial()
    {
        var mirror = new BufferMirror(11, 2);
        mirror.Upload();
        for (int i = 0; i < 4; i++) mirror.Write(i, 1);
        mirror.Write(10, 2);
        AtlasUploadPlan plan = mirror.Ranges.Plan();
        Assert.False(plan.Full);
        Assert.Equal(2, plan.RangeCount);
        mirror.Upload(plan);
    }

    [Theory]
    [InlineData(160, 32, new[] { 62, 63, 64, 65, 159 }, 62, 98)]
    [InlineData(100, 2, new[] { 3, 20, 40, 80 }, 3, 78)]
    [InlineData(100, 1, new[] { 99 }, 99, 1)]
    public void SingleRangeIncludesUnchangedGapsAndIgnoresFragmentBudget(int capacity, int budget, int[] indices,
        int expectedStart, int expectedCount)
    {
        var mirror = new BufferMirror(capacity, budget);
        mirror.Upload();
        foreach (int index in indices) mirror.Write(index, (byte)(index + 1));
        AtlasUploadPlan plan = mirror.Ranges.Plan(singleRange: true);
        Assert.False(plan.Full);
        Assert.False(mirror.Ranges.RequiresFullUpload);
        Assert.Equal(1, plan.RangeCount);
        Assert.Equal(new AtlasUploadRange(expectedStart, expectedCount), plan.GetRange(0));
        mirror.Upload(plan);
    }

    [Fact]
    public void SingleRangeUsesBoundedDenseSpanButFullForCapacityWideSpan()
    {
        var mirror = new BufferMirror(100);
        mirror.Upload();
        for (int i = 10; i < 70; i++) mirror.Write(i, 17);
        AtlasUploadPlan dense = mirror.Ranges.Plan(singleRange: true);
        Assert.False(dense.Full);
        Assert.Equal(new AtlasUploadRange(10, 60), dense.GetRange(0));
        mirror.Upload(dense);
        mirror.Write(0, 18);
        mirror.Write(99, 19);
        AtlasUploadPlan wide = mirror.Ranges.Plan(singleRange: true);
        Assert.True(wide.Full);
        Assert.True(mirror.Ranges.RequiresFullUpload);
        Assert.Equal(new AtlasUploadRange(0, 100), wide.GetRange(0));
        mirror.Upload(wide);
    }

    [Fact]
    public void SingleRangeRetainsInitialEmptyStructuralAndFailureRequirements()
    {
        var mirror = new BufferMirror(0);
        AtlasUploadPlan emptyInitial = mirror.Ranges.Plan(singleRange: true);
        Assert.True(emptyInitial.Full);
        Assert.Equal(0, emptyInitial.RangeCount);
        mirror.Upload(emptyInitial);
        AtlasUploadPlan emptyClean = mirror.Ranges.Plan(singleRange: true);
        Assert.False(emptyClean.Full);
        Assert.Equal(0, emptyClean.RangeCount);
        mirror.Upload(emptyClean);
        mirror.Replace(100);
        mirror.Write(30, 1);
        AtlasUploadPlan grown = mirror.Ranges.Plan(singleRange: true);
        Assert.True(grown.Full);
        mirror.Upload(grown);
        mirror.Cpu.AsSpan(0, BufferMirror.Stride).Fill(42);
        mirror.Ranges.RequireFull();
        AtlasUploadPlan compacted = mirror.Ranges.Plan(singleRange: true);
        Assert.True(compacted.Full);
        mirror.Upload(compacted);
        mirror.Write(20, 2);
        mirror.Write(40, 3);
        AtlasUploadPlan failed = mirror.Ranges.Plan(singleRange: true);
        Assert.False(failed.Full);
        mirror.Ranges.Fail();
        Assert.False(mirror.Ranges.Complete(failed.Version));
        AtlasUploadPlan retry = mirror.Ranges.Plan(singleRange: true);
        Assert.True(retry.Full);
        mirror.Upload(retry);
        mirror.Replace(100);
        mirror.Upload(mirror.Ranges.Plan(singleRange: true));
    }

    [Fact]
    public void SingleRangeReentryAndResizeCannotOverwriteBorrowedScratchOrLoseWrites()
    {
        var mirror = new BufferMirror(160);
        mirror.Upload();
        mirror.Write(63, 1);
        mirror.Write(129, 2);
        AtlasUploadPlan outer = mirror.Ranges.Plan(singleRange: true);
        AtlasUploadRange original = outer.GetRange(0);
        mirror.Write(5, 3);
        AtlasUploadPlan nested = mirror.Ranges.Plan();
        Assert.True(nested.Full);
        Assert.Equal(original, outer.GetRange(0));
        Assert.False(mirror.Ranges.Complete(nested.Version));
        Assert.False(mirror.Ranges.Complete(outer.Version));
        mirror.Upload(mirror.Ranges.Plan(singleRange: true));
        mirror.Write(70, 4);
        AtlasUploadPlan beforeResize = mirror.Ranges.Plan(singleRange: true);
        mirror.Replace(90);
        Assert.Equal(new AtlasUploadRange(70, 1), beforeResize.GetRange(0));
        Assert.False(mirror.Ranges.Complete(beforeResize.Version));
        mirror.Upload(mirror.Ranges.Plan(singleRange: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RandomizedPlansMatchEveryBufferByteIncludingUnchangedGaps(bool singleRange)
    {
        var random = new Random(413_027);
        var mirror = new BufferMirror(257, 3);
        for (int iteration = 0; iteration < 300; iteration++)
        {
            if (iteration % 37 == 0) mirror.Replace(random.Next(1, 400));
            if (iteration % 23 == 0)
            {
                // Model unindexed compaction/global edits; these invalidate tracking regardless of mode.
                random.NextBytes(mirror.Cpu);
                mirror.Ranges.RequireFull();
            }
            int writes = random.Next(0, mirror.Ranges.Capacity + 1);
            for (int i = 0; i < writes; i++)
                mirror.Write(random.Next(mirror.Ranges.Capacity), (byte)random.Next(256));
            AtlasUploadPlan plan = mirror.Ranges.Plan(singleRange);
            if (singleRange) Assert.InRange(plan.RangeCount, 0, 1);
            if (iteration % 17 == 0)
            {
                if (plan.RangeCount != 0) mirror.Copy(plan.GetRange(0));
                mirror.Write(random.Next(mirror.Ranges.Capacity), (byte)random.Next(256));
                Assert.False(mirror.Ranges.Complete(plan.Version));
                plan = mirror.Ranges.Plan(singleRange);
                Assert.True(plan.Full);
            }
            else if (iteration % 19 == 0)
            {
                if (plan.RangeCount != 0) mirror.Copy(plan.GetRange(0));
                mirror.Ranges.Fail();
                plan = mirror.Ranges.Plan(singleRange);
                Assert.True(plan.Full);
            }
            mirror.Upload(plan);
        }
    }

    [Fact]
    public void EmptyCapacitySupportsFullCompletionAndResize()
    {
        var mirror = new BufferMirror(0);
        AtlasUploadPlan plan = mirror.Ranges.Plan();
        Assert.True(plan.Full);
        Assert.Equal(0, plan.RangeCount);
        mirror.Upload(plan);
        Assert.False(mirror.Ranges.HasPendingChanges);
        mirror.Replace(5);
        mirror.Write(4, 123);
        mirror.Upload();
        mirror.Replace(0);
        mirror.Upload();
        Assert.False(mirror.Ranges.HasPendingChanges);
    }

    [Fact]
    public void CompactionReplacementAndCompatibilityGapRequireFullMirrors()
    {
        var mirror = new BufferMirror(20);
        mirror.Upload();
        mirror.Write(1, 10);
        mirror.Write(19, 20);
        mirror.Upload();
        // Compaction may change every surviving index, including indices never individually marked.
        mirror.Cpu.AsSpan(12 * BufferMirror.Stride, BufferMirror.Stride).Fill(42);
        mirror.Cpu.AsSpan(12 * BufferMirror.Stride, BufferMirror.Stride).CopyTo(mirror.Cpu.AsSpan(0));
        mirror.Ranges.RequireFull();
        AtlasUploadPlan compacted = mirror.Ranges.Plan();
        Assert.True(compacted.Full);
        mirror.Upload(compacted);
        mirror.Replace(35);
        mirror.Upload();
        mirror.Replace(35); // A new GPU resource at unchanged capacity still needs all existing bytes.
        mirror.Upload();
        mirror.Cpu.AsSpan().Fill(99); // An unobserved writer/compatibility gap invalidates dirty tracking.
        mirror.Ranges.RequireFull();
        mirror.Upload();
    }

    [Fact]
    public void RepeatedMarkInvalidatesCompletionAndReentrantPlanKeepsOriginalRangesFrozen()
    {
        var mirror = new BufferMirror(100);
        mirror.Upload();
        mirror.Write(2, 10);
        mirror.Write(10, 20);
        AtlasUploadPlan original = mirror.Ranges.Plan();
        long version = original.Version;
        mirror.Write(2, 11);
        Assert.True(mirror.Ranges.Version > version);
        Assert.Equal(2, mirror.Ranges.DirtyCount);
        AtlasUploadPlan nested = mirror.Ranges.Plan();
        Assert.True(nested.Full);
        Assert.Equal(new AtlasUploadRange(2, 1), original.GetRange(0));
        Assert.Equal(new AtlasUploadRange(10, 1), original.GetRange(1));
        Assert.False(mirror.Ranges.Complete(nested.Version));
        Assert.True(mirror.Ranges.RequiresFullUpload);
        Assert.False(mirror.Ranges.Complete(original.Version));
        mirror.Upload();
    }

    [Fact]
    public void ReentrantWriteAfterEarlierRangeWasCopiedRequiresFullRetry()
    {
        var mirror = new BufferMirror(100);
        mirror.Upload();
        mirror.Write(1, 10);
        mirror.Write(30, 20);
        AtlasUploadPlan plan = mirror.Ranges.Plan();
        mirror.Copy(plan.GetRange(0));
        mirror.Write(1, 99);
        mirror.Copy(plan.GetRange(1));
        Assert.False(mirror.Ranges.Complete(plan.Version));
        Assert.True(mirror.Ranges.HasPendingChanges);
        mirror.Upload();
    }

    [Fact]
    public void ReentrantPlanWithoutWritesCannotReleaseOrOverwriteOuterScratch()
    {
        var mirror = new BufferMirror(100);
        mirror.Upload();
        mirror.Write(1, 10);
        mirror.Write(30, 20);
        AtlasUploadPlan outer = mirror.Ranges.Plan();
        AtlasUploadPlan nested = mirror.Ranges.Plan();
        Assert.True(nested.Full);
        Assert.False(mirror.Ranges.Complete(nested.Version));
        mirror.Write(60, 30);
        Assert.True(mirror.Ranges.Plan().Full);
        Assert.Equal(new AtlasUploadRange(1, 1), outer.GetRange(0));
        Assert.Equal(new AtlasUploadRange(30, 1), outer.GetRange(1));
        Assert.False(mirror.Ranges.Complete(outer.Version));
        mirror.Upload();
    }

    [Fact]
    public void FailureAfterSeveralRegionsRetainsEveryByteForFullRetry()
    {
        var mirror = new BufferMirror(100);
        mirror.Upload();
        mirror.Write(1, 11);
        mirror.Write(20, 22);
        mirror.Write(80, 33);
        AtlasUploadPlan plan = mirror.Ranges.Plan();
        Assert.Throws<InvalidOperationException>(() =>
        {
            for (int i = 0; i < plan.RangeCount; i++)
            {
                if (i == 2) throw new InvalidOperationException("Injected upload failure");
                mirror.Copy(plan.GetRange(i));
            }
        });
        mirror.Ranges.Fail();
        Assert.True(mirror.Ranges.RequiresFullUpload);
        Assert.False(mirror.Ranges.Complete(plan.Version));
        mirror.Upload();
    }

    [Fact]
    public void ResizeDuringOutstandingPlanCannotClearReplacementWork()
    {
        var mirror = new BufferMirror(100);
        mirror.Upload();
        mirror.Write(1, 1);
        mirror.Write(99, 2);
        AtlasUploadPlan old = mirror.Ranges.Plan();
        mirror.Replace(25);
        Assert.Equal(new AtlasUploadRange(99, 1), old.GetRange(1));
        Assert.False(mirror.Ranges.Complete(old.Version));
        mirror.Upload();
    }

    [Fact]
    public void InvalidArgumentsDoNotErasePendingWrites()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AtlasDirtyRanges(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AtlasDirtyRanges(1, 0));
        var ranges = new AtlasDirtyRanges(10);
        Assert.True(ranges.Complete(ranges.Plan().Version));
        ranges.Mark(3);
        long version = ranges.Version;
        Assert.Throws<ArgumentOutOfRangeException>(() => ranges.Mark(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ranges.Mark(10));
        Assert.Throws<ArgumentOutOfRangeException>(() => ranges.Resize(-1));
        Assert.Equal(version, ranges.Version);
        Assert.Equal(1, ranges.DirtyCount);
        AtlasUploadPlan plan = ranges.Plan();
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.GetRange(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.GetRange(plan.RangeCount));
        Assert.True(ranges.Complete(plan.Version));
        Assert.False(ranges.Complete(plan.Version));
        Assert.True(ranges.RequiresFullUpload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmPartialAndFullPlansAllocateNoManagedBytes(bool singleRange)
    {
        var ranges = new AtlasDirtyRanges(256, 8);
        _ = MeasurePlanCycles(ranges, singleRange, out _);
        // Runtime hooks installed by other tests may do one-time JIT bookkeeping. Require an actual
        // zero-allocation steady-state window; persistent per-plan allocations fail every window.
        long minimumAllocated = long.MaxValue;
        for (int window = 0; window < 3; window++)
        {
            long allocated = MeasurePlanCycles(ranges, singleRange, out int checksum);
            Assert.True(checksum > 0);
            minimumAllocated = Math.Min(minimumAllocated, allocated);
        }
        Assert.Equal(0, minimumAllocated);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static long MeasurePlanCycles(AtlasDirtyRanges ranges, bool singleRange, out int checksum)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        checksum = 0;
        for (int i = 0; i < 10_000; i++) checksum += PlanCycle(ranges, i, singleRange);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static int PlanCycle(AtlasDirtyRanges ranges, int iteration, bool singleRange)
    {
        ranges.Mark(iteration % ranges.Capacity);
        ranges.Mark((iteration + 64) % ranges.Capacity);
        if ((iteration & 7) == 0) ranges.RequireFull();
        AtlasUploadPlan plan = ranges.Plan(singleRange);
        int checksum = 0;
        for (int i = 0; i < plan.RangeCount; i++) checksum += plan.GetRange(i).Count;
        if (!ranges.Complete(plan.Version)) throw new InvalidOperationException("Unchanged plan failed");
        return checksum;
    }

    private sealed class BufferMirror
    {
        internal const int Stride = 7;
        internal readonly AtlasDirtyRanges Ranges;
        internal byte[] Cpu;
        private byte[] _gpu;

        internal BufferMirror(int capacity, int budget = 32)
        {
            Ranges = new AtlasDirtyRanges(capacity, budget);
            Cpu = new byte[checked(capacity * Stride)];
            _gpu = new byte[Cpu.Length];
            for (int i = 0; i < Cpu.Length; i++) Cpu[i] = (byte)(i % 251);
        }

        internal void Write(int index, byte value)
        {
            Cpu.AsSpan(checked(index * Stride), Stride).Fill(value);
            Ranges.Mark(index);
        }

        internal void Replace(int capacity)
        {
            Array.Resize(ref Cpu, checked(capacity * Stride));
            _gpu = new byte[Cpu.Length];
            Ranges.Resize(capacity);
            Assert.Equal(capacity, Ranges.Capacity);
            Assert.True(Ranges.RequiresFullUpload);
        }

        internal void Copy(AtlasUploadRange range)
        {
            int start = checked(range.Start * Stride);
            int count = checked(range.Count * Stride);
            Cpu.AsSpan(start, count).CopyTo(_gpu.AsSpan(start, count));
        }

        internal void Upload() => Upload(Ranges.Plan());

        internal void Upload(AtlasUploadPlan plan)
        {
            for (int i = 0; i < plan.RangeCount; i++) Copy(plan.GetRange(i));
            Assert.True(Ranges.Complete(plan.Version));
            Assert.Equal(Cpu, _gpu);
        }
    }
}
