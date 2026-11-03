using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Cosmoteer.Ships.Rendering;
using Halfling.Graphics;
using Halfling.Graphics.D3D11;
using Halfling.Windows.D3D11;
using HarmonyLib;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Xunit;
using D3DBuffer = SharpDX.Direct3D11.Buffer;

namespace UnofficialPatch.Tests;

public sealed class AtlasRangeUploadTests : IDisposable
{
    private readonly Warp _warp = new();

    public void Dispose() => _warp.Dispose();

    [Fact]
    public void KillSwitchDisablesOnlyOnZero()
    {
        Assert.False(AtlasRangeUpload.IsRequested("0"));
        Assert.True(AtlasRangeUpload.IsRequested(null));
        Assert.True(AtlasRangeUpload.IsRequested("1"));
    }

    private static (int, int)[] Spans(AtlasDirtySpans s) => Enumerable.Range(0, s.Count).Select(i => (s.Lo(i), s.Hi(i))).ToArray();

    [Fact]
    public void SpansMergeOverlappingAndAdjacentAndKeepOrder()
    {
        var s = new AtlasDirtySpans();
        s.Add(10, 11);
        s.Add(5, 5); // empty
        s.Add(11, 12); // adjacent
        s.Add(2, 3);
        s.Add(20, 25);
        Assert.Equal([(2, 3), (10, 12), (20, 25)], Spans(s));
        s.Add(3, 21); // bridges all three
        Assert.Equal([(2, 25)], Spans(s));
        s.Clear();
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void SpansCapMergesSmallestGap()
    {
        var s = new AtlasDirtySpans();
        foreach (int i in new[] { 0, 10, 100, 1000 }) s.Add(i, i + 1);
        Assert.Equal(AtlasDirtySpans.MaxSpans, s.Count);
        s.Add(12, 13); // gap 1 to [10,11) is the smallest
        Assert.Equal([(0, 1), (10, 13), (100, 101), (1000, 1001)], Spans(s));
        s.Add(5000, 5001); // smallest gap now [0,1)-[10,13)
        Assert.Equal([(0, 13), (100, 101), (1000, 1001), (5000, 5001)], Spans(s));
    }

    [Fact]
    public void SpansFuzzCoverEveryMarkWithinBoundsAndCap()
    {
        var rng = new Random(20261031);
        for (int round = 0; round < 2000; round++)
        {
            var s = new AtlasDirtySpans();
            var marked = new bool[300];
            int min = int.MaxValue, max = int.MinValue;
            for (int m = rng.Next(1, 40); m > 0; m--)
            {
                int lo = rng.Next(0, 300), hi = Math.Min(300, lo + rng.Next(1, 8));
                s.Add(lo, hi);
                for (int i = lo; i < hi; i++) marked[i] = true;
                min = Math.Min(min, lo);
                max = Math.Max(max, hi);
                Assert.InRange(s.Count, 1, AtlasDirtySpans.MaxSpans);
            }
            for (int k = 0; k < s.Count; k++)
            {
                Assert.True(s.Lo(k) < s.Hi(k));
                if (k > 0) Assert.True(s.Hi(k - 1) < s.Lo(k)); // sorted, disjoint, non-adjacent
            }
            Assert.Equal(min, s.Lo(0));
            Assert.Equal(max, s.Hi(s.Count - 1));
            for (int i = 0; i < marked.Length; i++)
                if (marked[i]) Assert.Contains(Enumerable.Range(0, s.Count), k => s.Lo(k) <= i && i < s.Hi(k));
        }
    }

    [Fact]
    public void TranspilersMatchPinnedIlAndReplaceOneListCall()
    {
        foreach ((MethodBase target, _) in AtlasRangeUpload.Targets)
        {
            List<CodeInstruction> code = AtlasRangeUpload.Transpiler(PatchProcessor.GetOriginalInstructions(target), target).ToList();
            Assert.Single(code, ci => ci.operand is MethodInfo { DeclaringType.Name: nameof(AtlasRangeUpload) });
        }
    }

    [Fact]
    public void NativeContractsArePinned() => AtlasRangeUpload.ValidateNative();

    [Fact]
    public void BypassedRemovalsAreForeignOwnerDependencies()
    {
        var dependencies = AtlasRangeUpload.Dependencies.Select(m => (m.Name, m.DeclaringType)).ToList();
        Assert.Contains(("RemoveAt", typeof(GraphicsList<AtlasQuad>)), dependencies);
        Assert.Contains(("RemoveRange", typeof(GraphicsList<AtlasQuad>)), dependencies);
    }

    [Fact]
    public void PatchesAreEmptyOnlyWhenKilled() =>
        Assert.Equal(AtlasRangeUpload.Requested ? AtlasRangeUpload.Targets.Length : 0, AtlasRangeUpload.Patches.Length);

    // ---- Real D3D11 device (WARP, headless) with staging readback ---------------------------------------------------

    /// <summary>D3D11GraphicsManager over a WARP device; only the members the arrays use are set.</summary>
    private sealed class Warp : IDisposable
    {
        internal readonly Device Device = new(DriverType.Warp, DeviceCreationFlags.None);
        internal readonly D3D11GraphicsManager Gfx;

        internal Warp()
        {
            var window = (D3D11Window)RuntimeHelpers.GetUninitializedObject(typeof(D3D11Window));
#pragma warning disable CS9216 // stored into the property's backing field, used as a Lock by the manager
            AccessTools.Field(typeof(D3D11Window), "<ContextLock>k__BackingField").SetValue(window, new Lock());
#pragma warning restore CS9216
            Gfx = (D3D11GraphicsManager)RuntimeHelpers.GetUninitializedObject(typeof(D3D11GraphicsManager));
#pragma warning disable CA1816 // the shell manager owns no device of its own
            GC.SuppressFinalize(Gfx);
#pragma warning restore CA1816
            Set("<Window>k__BackingField", window);
            Set("<Device>k__BackingField", Device);
            Set("<Context>k__BackingField", Device.ImmediateContext);
            Set("_ownedDisposables", new ConcurrentDictionary<WeakReference<IDisposable>, object?>());
        }

        private void Set(string field, object value) =>
            (typeof(D3D11GraphicsManager).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            ?? throw new MissingFieldException(field)).SetValue(Gfx, value);

        internal unsafe byte[] Read(GraphicsArray<AtlasQuad> array, int count)
        {
            D3DBuffer source = ((D3D11BaseGraphicsArray<AtlasQuad>)array)._vertices!;
            using var staging = new D3DBuffer(Device, new BufferDescription
            {
                SizeInBytes = source.Description.SizeInBytes,
                Usage = ResourceUsage.Staging,
                CpuAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
            });
            Device.ImmediateContext.CopyResource(source, staging);
            SharpDX.DataBox box = Device.ImmediateContext.MapSubresource(staging, 0, MapMode.Read, MapFlags.None);
            try { return new ReadOnlySpan<byte>((void*)box.DataPointer, count * sizeof(AtlasQuad)).ToArray(); }
            finally { Device.ImmediateContext.UnmapSubresource(staging, 0); }
        }

        public void Dispose() => Device.Dispose();
    }

    private static unsafe AtlasQuad Quad(Random rng)
    {
        AtlasQuad q = default;
        rng.NextBytes(new Span<byte>(&q, sizeof(AtlasQuad)));
        return q;
    }

    private static unsafe byte[] Shadow(GraphicsArray<AtlasQuad> array, int count) =>
        new ReadOnlySpan<byte>(array.GetUnsafeTempPointer(readOnly: true), count * sizeof(AtlasQuad)).ToArray();

    private static void Upload(GraphicsArray<AtlasQuad> array) => ((D3D11BaseGraphicsArray<AtlasQuad>)array).UpdateDirtyData();

    private static AtlasDirtySpans Pending(AtlasRangeArray a) => (AtlasDirtySpans)AccessTools.Field(typeof(AtlasRangeArray), "_spans").GetValue(a)!;

    private static unsafe bool Same(byte[] a, byte[] b, int index) =>
        a.AsSpan(index * sizeof(AtlasQuad), sizeof(AtlasQuad)).SequenceEqual(b.AsSpan(index * sizeof(AtlasQuad), sizeof(AtlasQuad)));

    [Fact]
    public unsafe void OffsetUploadsSendOnlyMarkedSpansAndPreserveTheRest()
    {
        var rng = new Random(1);
        using var a = new AtlasRangeArray(_warp.Gfx, 64);
        for (int i = 0; i < 64; i++) a[i] = Quad(rng);
        Upload(a);
        Assert.Equal(Shadow(a, 64), _warp.Read(a, 64));
        byte[] before = _warp.Read(a, 64);
        a[5] = Quad(rng);
        a[40] = Quad(rng);
        a[41] = Quad(rng);
        a.GetUnsafeTempPointer(readOnly: true)[20] = Quad(rng); // unmarked: must not reach the GPU
        Assert.Equal([(5, 6), (40, 42)], Spans(Pending(a)));
        Upload(a);
        Assert.Equal(0, Pending(a).Count);
        byte[] gpu = _warp.Read(a, 64), cpu = Shadow(a, 64);
        for (int i = 0; i < 64; i++)
            Assert.True(i == 20 ? Same(gpu, before, i) && !Same(gpu, cpu, i) : Same(gpu, cpu, i), "element " + i);
    }

    [Fact]
    public void ResizeKeepsEveryWrittenElement()
    {
        var rng = new Random(2);
        using var a = new AtlasRangeArray(_warp.Gfx, 32);
        for (int i = 0; i < 32; i++) a[i] = Quad(rng);
        Upload(a);
        a.Resize(100);
        a[90] = Quad(rng);
        Assert.Equal([(0, 32), (90, 91)], Spans(Pending(a)));
        Upload(a);
        byte[] gpu = _warp.Read(a, 100), cpu = Shadow(a, 100);
        for (int i = 0; i < 100; i++)
            if (i < 32 || i == 90) Assert.True(Same(gpu, cpu, i), "element " + i);
    }

    [Fact]
    public void RefWriteAfterUploadIsSentByTheNextUpload()
    {
        var rng = new Random(3);
        using var a = new AtlasRangeArray(_warp.Gfx, 8);
        for (int i = 0; i < 8; i++) a[i] = Quad(rng);
        Upload(a);
        ref AtlasQuad slot = ref a.GetRef(3);
        Upload(a); // runs between the mark and the caller's write
        slot = Quad(rng);
        Upload(a);
        Assert.Equal(Shadow(a, 8), _warp.Read(a, 8));
        Upload(a);
        Assert.Equal(0, Pending(a).Count);
    }

    [Fact]
    public void SwapRemovalSequenceMatchesNativeDynamicArray()
    {
        var rng = new Random(4);
        var native = new GraphicsList<AtlasQuad>(_warp.Gfx, 32, PerformanceMode.Dynamic);
        GraphicsList<AtlasQuad> range = AtlasRangeUpload.CreateRangeList(_warp.Gfx);
        Assert.IsType<D3D11DynamicGraphicsArray<AtlasQuad>>(native.InternalArray);
        Assert.IsType<AtlasRangeArray>(range.InternalArray);
        try
        {
            for (int op = 0; op < 4000; op++)
            {
                int roll = rng.Next(10);
                if (native.Count == 0 || roll < 5)
                {
                    AtlasQuad q = Quad(rng);
                    native.Add(q);
                    range.Add(q);
                }
                else if (roll < 8)
                {
                    int i = rng.Next(native.Count);
                    AtlasQuad q = Quad(rng);
                    native[i] = q;
                    range[i] = q;
                }
                else
                {
                    // InternalManagedAtlasQuad.Dispose/MoveIndex: move the last quad over the removed one, remove the last.
                    int i = rng.Next(native.Count), last = native.Count - 1;
                    native[i] = native[last];
                    native.RemoveAt(last);
                    range[i] = range[last];
                    AtlasRangeUpload.RemoveAt(range, last);
                }
                if (op % 7 == 0)
                {
                    Upload(native.InternalArray);
                    Upload(range.InternalArray);
                }
            }
            Upload(native.InternalArray);
            Upload(range.InternalArray);
            Assert.Equal(native.Count, range.Count);
            Assert.Equal(_warp.Read(native.InternalArray, native.Count), _warp.Read(range.InternalArray, range.Count));
            Assert.Equal(Shadow(range.InternalArray, range.Count), _warp.Read(range.InternalArray, range.Count));
        }
        finally
        {
            native.Dispose();
            range.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentWritesAndResizesDuringUploadsAreNotLost()
    {
        var a = new AtlasRangeArray(_warp.Gfx, 256);
        CancellationToken token = TestContext.Current.CancellationToken;
        Task writer = Task.Run(() =>
        {
            var rng = new Random(5);
            for (int n = 0; n < 200_000; n++) a[rng.Next(256)] = Quad(rng);
        }, token);
        Task resizer = Task.Run(() =>
        {
            for (int k = 1; k <= 40; k++) { a.Resize(256 + k * 16); Thread.Yield(); }
        }, token);
        while (!writer.IsCompleted || !resizer.IsCompleted) Upload(a);
        await Task.WhenAll(writer, resizer);
        Upload(a);
        Assert.Equal(Shadow(a, 256), _warp.Read(a, 256));
        await DisposeAfter(writer, resizer, a);
    }

    /// <summary>The workers are finished (awaited above); disposal must not race them.</summary>
    private static async Task DisposeAfter(Task writer, Task resizer, AtlasRangeArray array)
    {
        await Task.WhenAll(writer, resizer).ConfigureAwait(false);
        array.Dispose();
    }

    /// <summary>Render thread: ContextLock first (as native BeginDraw), then a write to the array a worker is uploading.</summary>
    private static void RenderWrite(Lock context, TaskCompletionSource held, AtlasRangeArray array, int index, AtlasQuad quad)
    {
        using (context.EnterScope())
        {
            held.SetResult();
            Thread.Sleep(5);
            array[index] = quad;
        }
    }

    [Fact]
    public async Task UploadHintWhileRenderHoldsContextLockDoesNotDeadlock()
    {
        var a = new AtlasRangeArray(_warp.Gfx, 64);
        var rng = new Random(7);
        CancellationToken token = TestContext.Current.CancellationToken;
        Lock context = _warp.Gfx.ContextLock;
        for (int round = 0; round < 20; round++)
        {
            a[round] = Quad(rng);
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int index = 63 - round;
            AtlasQuad quad = Quad(rng);
            AtlasRangeArray target = a;
            Task render = Task.Run(() => RenderWrite(context, held, target, index, quad), token);
            await held.Task.WaitAsync(token);
            Task hint = Task.Run(target.UploadHint, token);
            await Task.WhenAll(render, hint).WaitAsync(TimeSpan.FromSeconds(10), token);
        }
        a.UploadHint();
        byte[] gpu = _warp.Read(a, 64), cpu = Shadow(a, 64);
        for (int i = 0; i < 64; i++)
            if (i < 20 || i >= 44) Assert.True(Same(gpu, cpu, i), "element " + i);
        a.Dispose();
    }

    [Fact]
    public void LastRemovalOnlyShrinksCountAndForeignOwnerKeepsNative()
    {
        GraphicsList<AtlasQuad> list = AtlasRangeUpload.CreateRangeList(_warp.Gfx);
        var a = (AtlasRangeArray)list.InternalArray;
        try
        {
            var rng = new Random(6);
            for (int i = 0; i < 4; i++) list.Add(Quad(rng));
            Upload(a);
            AtlasRangeUpload.RemoveAt(list, 3);
            Assert.Equal(3, list.Count);
            Assert.Equal(0, Pending(a).Count); // nothing copied, nothing to upload
            AtlasRangeUpload.SetForeign("test owner");
            try
            {
                AtlasRangeUpload.RemoveAt(list, 2); // native: zero-length CopyTo marks the whole array
                Assert.Equal(2, list.Count);
                Assert.Equal([(0, a.Size)], Spans(Pending(a)));
            }
            finally { AtlasRangeUpload.SetForeign(null); }
            list.Clear();
            Assert.Throws<ArgumentOutOfRangeException>(() => AtlasRangeUpload.RemoveAt(list, -1));
        }
        finally { list.Dispose(); }
    }
}
