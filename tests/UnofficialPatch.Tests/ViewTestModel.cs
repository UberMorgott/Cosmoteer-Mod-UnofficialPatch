using System;
using System.Collections.Generic;
using Xunit;

namespace UnofficialPatch.Tests;

/// <summary>Toy payload model: ship 16 B, layer 8 B, quad 48 B (4 verts x [8 B position, 4 B colour]), part 12 B.</summary>
internal sealed class TestPayloadModel : IViewPayloadModel
{
    public const int Ship = 0, Layer = 1, Quad = 2, Part = 3;
    public const int VertBytes = 12, ColourOffset = 8;
    private static readonly int[] Sizes = [16, 8, 4 * VertBytes, 12];

    public int KindCount => 4;
    public int FullSize(int kind) => Sizes[kind];
    public int ColourMaxSize(int kind) => kind == Quad ? 16 : 0;
    public bool IsValidColourLength(int kind, int length) => kind == Quad && length is 4 or 16;

    public void ApplyColour(int kind, Span<byte> full, ReadOnlySpan<byte> colour)
    {
        for (int v = 0; v < 4; v++)
        {
            colour.Slice(colour.Length == 4 ? 0 : v * 4, 4).CopyTo(full.Slice(v * VertBytes + ColourOffset, 4));
        }
    }

    public static int ParentKindOf(int kind) => kind switch { Layer => Ship, Quad => Layer, Part => Ship, _ => -1 };
}

/// <summary>
/// Sim-side authoritative store for the protocol tests (the spec's <c>Authority</c>): slot maps, flat value arrays,
/// parent links. Implements <see cref="IViewSource"/>; allocation-free once its arrays have grown.
/// </summary>
internal sealed class TestAuthority : IViewSource
{
    private readonly ViewWriter _writer;
    private readonly ViewSlotMap[] _slots = [new(), new(), new(), new()];
    private readonly byte[][] _data = new byte[4][];
    private readonly uint[][] _gen = new uint[4][];
    private readonly int[][] _parent = new int[4][];
    private readonly uint[][] _parentGen = new uint[4][];
    private readonly bool[][] _doomed = new bool[4][];
    private readonly TestPayloadModel _model = new();

    public TestAuthority(ViewWriter writer)
    {
        _writer = writer;
        for (int k = 0; k < 4; k++)
        {
            _data[k] = new byte[64 * _model.FullSize(k)];
            _gen[k] = new uint[64];
            _parent[k] = new int[64];
            _parentGen[k] = new uint[64];
            _doomed[k] = new bool[64];
        }
    }

    public long Tick { get; private set; }
    public ViewWriter Writer => _writer;
    public ViewSlotMap Slots(int kind) => _slots[kind];

    public ViewId Create(int kind, ViewId parent, ReadOnlySpan<byte> initial)
    {
        int pk = TestPayloadModel.ParentKindOf(kind);
        if (pk >= 0 && !IsAlive(pk, parent))
        {
            throw new InvalidOperationException("parent not alive");
        }
        ViewId id = _slots[kind].Alloc();
        Grow(kind, id.Slot);
        initial.CopyTo(Data(kind, id.Slot));
        _gen[kind][id.Slot] = id.Gen;
        _parent[kind][id.Slot] = pk < 0 ? -1 : (int)parent.Slot;
        _parentGen[kind][id.Slot] = parent.Gen;
        _doomed[kind][id.Slot] = false;
        _writer.Create(kind, id, pk, pk < 0 ? ViewId.None : parent);
        return id;
    }

    /// <summary>Alive and not doomed by a lazy parent destroy (the oracle's notion of alive).</summary>
    public bool IsAlive(int kind, ViewId id) => _slots[kind].TryResolve(id) && !_doomed[kind][id.Slot];

    /// <summary>Still resolvable sim-side (doomed children included): value writes are still legal.</summary>
    public bool IsResolvable(int kind, ViewId id) => _slots[kind].TryResolve(id);

    public void SetFull(int kind, ViewId id, ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(Data(kind, id.Slot));
        _writer.SetValue(kind, id);
    }

    public void SetColour(ViewId quad, ReadOnlySpan<byte> colours)
    {
        _model.ApplyColour(TestPayloadModel.Quad, Data(TestPayloadModel.Quad, quad.Slot), colours);
        _writer.SetColour(TestPayloadModel.Quad, quad);
    }

    /// <summary>Logs Destroy for the object only; descendants are freed without a log entry (the reader cascades).
    /// With <paramref name="lazy"/> they stay resolvable (doomed) until <see cref="Reap"/>, like natively disposed
    /// children whose own dispose comes later.</summary>
    public void Destroy(int kind, ViewId id, bool lazy = false)
    {
        _writer.Destroy(kind, id);
        FreeDescendants(kind, id, lazy);
        _slots[kind].Free(id);
    }

    public void Reap()
    {
        for (int k = 0; k < 4; k++)
        {
            for (uint s = 0; s < _slots[k].Count; s++)
            {
                if (_doomed[k][s])
                {
                    _doomed[k][s] = false;
                    _slots[k].Free(new ViewId(s, _gen[k][s]));
                }
            }
        }
    }

    /// <summary>Negative case: the sim frees an object without logging any Destroy (a protocol bug the writer must catch).</summary>
    public void FreeSilently(int kind, ViewId id) => _slots[kind].Free(id);

    public bool Emit(int kind, ReadOnlySpan<byte> payload) => _writer.Emit(kind, payload);

    public ViewPublishResult Publish()
    {
        Tick++;
        return _writer.Publish(this);
    }

    public Span<byte> Data(int kind, uint slot) => _data[kind].AsSpan((int)slot * _model.FullSize(kind), _model.FullSize(kind));

    // ---- IViewSource ----
    public bool TryWriteFull(int kind, ViewId id, Span<byte> dst)
    {
        if (!_slots[kind].TryResolve(id))
        {
            return false;
        }
        Data(kind, id.Slot).CopyTo(dst);
        return true;
    }

    public int TryWriteColour(int kind, ViewId id, Span<byte> dst)
    {
        if (!_slots[kind].TryResolve(id))
        {
            return -1;
        }
        Span<byte> d = Data(kind, id.Slot);
        bool uniform = true;
        for (int v = 1; v < 4 && uniform; v++)
        {
            uniform = d.Slice(v * TestPayloadModel.VertBytes + TestPayloadModel.ColourOffset, 4).SequenceEqual(d.Slice(TestPayloadModel.ColourOffset, 4));
        }
        for (int v = 0; v < (uniform ? 1 : 4); v++)
        {
            d.Slice(v * TestPayloadModel.VertBytes + TestPayloadModel.ColourOffset, 4).CopyTo(dst.Slice(v * 4, 4));
        }
        return uniform ? 4 : 16;
    }

    public int SnapshotLength => 16;

    public void WriteSnapshot(Span<byte> dst)
    {
        BitConverter.TryWriteBytes(dst, Tick);
        BitConverter.TryWriteBytes(dst[8..], Tick * 31);
    }

    private void FreeDescendants(int kind, ViewId id, bool lazy)
    {
        for (int k = 0; k < 4; k++)
        {
            if (TestPayloadModel.ParentKindOf(k) != kind)
            {
                continue;
            }
            for (uint s = 0; s < _slots[k].Count; s++)
            {
                var child = new ViewId(s, _gen[k][s]);
                if (_parent[k][s] != (int)id.Slot || _parentGen[k][s] != id.Gen || _doomed[k][s] || !_slots[k].TryResolve(child))
                {
                    continue;
                }
                FreeDescendants(k, child, lazy);
                if (lazy)
                {
                    _doomed[k][s] = true;
                }
                else
                {
                    _slots[k].Free(child);
                }
            }
        }
    }

    private void Grow(int kind, uint slot)
    {
        int size = _parent[kind].Length;
        if (slot < size)
        {
            return;
        }
        while (size <= slot)
        {
            size *= 2;
        }
        Array.Resize(ref _data[kind], size * _model.FullSize(kind));
        Array.Resize(ref _gen[kind], size);
        Array.Resize(ref _parent[kind], size);
        Array.Resize(ref _parentGen[kind], size);
        Array.Resize(ref _doomed[kind], size);
    }
}

/// <summary>
/// Independent reference model (history oracle): plain dictionaries, written separately from the implementation. It
/// applies the same logical operations (create / destroy with cascade / full write / colour write / emit) and records a
/// deep snapshot of the visible state at every publish, so the reader's drawable can be checked against the state at
/// exactly the publish point it committed.
/// </summary>
internal sealed class ViewOracle
{
    internal sealed class Obj
    {
        public uint Gen;
        public int ParentKind;
        public ViewId Parent;
        public byte[] Data = [];
    }

    internal sealed class Snapshot(Dictionary<(int Kind, uint Slot), Obj> objects, long tick)
    {
        public Dictionary<(int Kind, uint Slot), Obj> Objects { get; } = objects;
        public long Tick { get; } = tick;
    }

    private readonly Dictionary<(int Kind, uint Slot), Obj> _objects = [];
    private readonly Dictionary<long, Snapshot> _history = [];

    public List<(int Kind, byte[] Payload)> Events { get; } = [];
    public int ObjectCount => _objects.Count;

    public void Create(int kind, ViewId id, int parentKind, ViewId parent, ReadOnlySpan<byte> data)
    {
        Assert.False(_objects.ContainsKey((kind, id.Slot)), "oracle: slot already alive");
        _objects[(kind, id.Slot)] = new Obj { Gen = id.Gen, ParentKind = parentKind, Parent = parent, Data = data.ToArray() };
    }

    public void Destroy(int kind, ViewId id)
    {
        if (!Has(kind, id))
        {
            return;
        }
        _objects.Remove((kind, id.Slot));
        var dead = new Queue<(int Kind, ViewId Id)>();
        dead.Enqueue((kind, id));
        while (dead.Count > 0)
        {
            (int pk, ViewId pid) = dead.Dequeue();
            foreach (KeyValuePair<(int Kind, uint Slot), Obj> kv in new List<KeyValuePair<(int Kind, uint Slot), Obj>>(_objects))
            {
                if (kv.Value.ParentKind == pk && kv.Value.Parent == pid)
                {
                    _objects.Remove(kv.Key);
                    dead.Enqueue((kv.Key.Kind, new ViewId(kv.Key.Slot, kv.Value.Gen)));
                }
            }
        }
    }

    public bool Has(int kind, ViewId id) => _objects.TryGetValue((kind, id.Slot), out Obj? o) && o.Gen == id.Gen;

    public void SetFull(int kind, ViewId id, ReadOnlySpan<byte> data)
    {
        if (Has(kind, id))
        {
            _objects[(kind, id.Slot)].Data = data.ToArray();
        }
    }

    /// <summary>Own colour layout: vertex v's colour lives at bytes [v*12+8, v*12+12); 4 B = one colour for all.</summary>
    public void SetColour(ViewId quad, ReadOnlySpan<byte> colours)
    {
        if (!Has(TestPayloadModel.Quad, quad))
        {
            return;
        }
        byte[] d = _objects[(TestPayloadModel.Quad, quad.Slot)].Data;
        for (int v = 0; v < 4; v++)
        {
            for (int b = 0; b < 4; b++)
            {
                d[v * 12 + 8 + b] = colours.Length == 4 ? colours[b] : colours[v * 4 + b];
            }
        }
    }

    public void Emit(int kind, ReadOnlySpan<byte> payload) => Events.Add((kind, payload.ToArray()));

    public void Record(long seq, long tick)
    {
        var copy = new Dictionary<(int Kind, uint Slot), Obj>();
        foreach (KeyValuePair<(int Kind, uint Slot), Obj> kv in _objects)
        {
            copy[kv.Key] = new Obj { Gen = kv.Value.Gen, ParentKind = kv.Value.ParentKind, Parent = kv.Value.Parent, Data = (byte[])kv.Value.Data.Clone() };
        }
        lock (_history)
        {
            _history[seq] = new Snapshot(copy, tick);
        }
    }

    /// <summary>Drops recorded states older than <paramref name="seq"/> (the reader never commits an older one).</summary>
    public void Prune(long seq)
    {
        lock (_history)
        {
            var old = new List<long>();
            foreach (long s in _history.Keys)
            {
                if (s < seq)
                {
                    old.Add(s);
                }
            }
            foreach (long s in old)
            {
                _history.Remove(s);
            }
        }
    }

    public Snapshot At(long seq)
    {
        lock (_history)
        {
            return _history[seq];
        }
    }

    /// <summary>Exact comparison of a drawable with a recorded state: same live (kind, slot, gen), parents, bytes, tick.</summary>
    public static string? Diff(ViewTable d, Snapshot s)
    {
        int alive = 0;
        for (int k = 0; k < d.KindCount; k++)
        {
            for (uint slot = 0; slot < d.SlotCapacity(k); slot++)
            {
                if (!d.IsAlive(k, slot, out ViewId id))
                {
                    continue;
                }
                alive++;
                if (!s.Objects.TryGetValue((k, slot), out Obj? o) || o.Gen != id.Gen)
                {
                    return $"drawable has {k}/{id} which the oracle does not";
                }
                int pk = d.ParentOf(k, slot, out ViewId parent);
                if (pk != o.ParentKind || (pk >= 0 && parent != o.Parent))
                {
                    return $"parent of {k}/{id}: {pk}/{parent} vs {o.ParentKind}/{o.Parent}";
                }
                if (!d.DataOf(k, slot).SequenceEqual(o.Data))
                {
                    return $"data of {k}/{id} differs";
                }
            }
        }
        if (alive != s.Objects.Count)
        {
            return $"drawable has {alive} objects, oracle {s.Objects.Count}";
        }
        if (d.SnapshotLength != 16 || BitConverter.ToInt64(d.Snapshot) != s.Tick || BitConverter.ToInt64(d.Snapshot[8..]) != s.Tick * 31)
        {
            return $"snapshot tick differs (oracle {s.Tick})";
        }
        return null;
    }
}

/// <summary>Event sink that records each dispatch and can be told to throw on chosen attempts.</summary>
internal sealed class RecordingSink : IViewEventSink
{
    public List<(long Cs, int Kind, byte[] Payload)> Dispatched { get; } = [];
    public HashSet<int> ThrowOnAttempts { get; } = [];
    public double ThrowProbability { get; set; }
    public Random? Rng { get; set; }
    public int Attempts { get; private set; }

    public void Dispatch(long cs, int kind, ReadOnlySpan<byte> payload)
    {
        int attempt = Attempts++;
        if (ThrowOnAttempts.Contains(attempt) || (Rng != null && Rng.NextDouble() < ThrowProbability))
        {
            // Before any work: the contract allows a retry.
            throw new ViewEventRetryableException("injected dispatch fault");
        }
        if (PoisonKind == kind)
        {
            throw new ViewEventRetryableException("poisoned command");
        }
        Dispatched.Add((cs, kind, payload.ToArray()));
        if (ThrowAfterWorkOnAttempts.Contains(attempt) || (Rng != null && Rng.NextDouble() < AfterWorkProbability))
        {
            // After the visible side effect: uncertain, must never be repeated.
            throw new InvalidOperationException("injected fault after the side effect");
        }
    }

    /// <summary>Attempts that throw a generic exception after recording the side effect.</summary>
    public HashSet<int> ThrowAfterWorkOnAttempts { get; } = [];
    public double AfterWorkProbability { get; set; }

    /// <summary>Commands of this kind always fail retryably (a poisoned command); -1 = none.</summary>
    public int PoisonKind { get; set; } = -1;
}

/// <summary>Allocation-free sink: count and order checksum.</summary>
internal sealed class CountingSink : IViewEventSink
{
    public long Count;
    public long Sum;
    public long LastCs;

    public void Dispatch(long cs, int kind, ReadOnlySpan<byte> payload)
    {
        if (cs <= LastCs)
        {
            throw new InvalidOperationException("event order");
        }
        LastCs = cs;
        Count++;
        Sum += kind + payload.Length;
    }
}

/// <summary>Writer + ring + reader + authority + oracle, driven by the same logical ops.</summary>
internal sealed class ViewHarness
{
    public readonly TestPayloadModel Model = new();
    public readonly ViewRing<ViewPublication> Ring = new(new ViewPublication(), new ViewPublication(), new ViewPublication());
    public readonly ViewWriter Writer;
    public readonly ViewReader Reader;
    public readonly TestAuthority A;
    public readonly ViewOracle Oracle = new();
    public readonly RecordingSink Sink = new();
    private readonly Random _rng;
    private int _eventCounter;

    public ViewHarness(ViewWriterMode mode = ViewWriterMode.FailClosed, int seed = 1)
    {
        Writer = new ViewWriter(Ring, Model, mode);
        Reader = new ViewReader(Ring, Model, new ViewTable(Model));
        A = new TestAuthority(Writer);
        _rng = new Random(seed);
    }

    public ViewTable D => Reader.Drawable;

    public byte[] RandomBytes(int kind)
    {
        var b = new byte[Model.FullSize(kind)];
        _rng.NextBytes(b);
        return b;
    }

    public byte[] RandomColours(bool uniform)
    {
        var c = new byte[uniform ? 4 : 16];
        _rng.NextBytes(c);
        return c;
    }

    public ViewId Create(int kind, ViewId parent = default)
    {
        byte[] data = RandomBytes(kind);
        ViewId id = A.Create(kind, parent, data);
        int pk = TestPayloadModel.ParentKindOf(kind);
        Oracle.Create(kind, id, pk, pk < 0 ? ViewId.None : parent, data);
        return id;
    }

    public void SetFull(int kind, ViewId id)
    {
        byte[] data = RandomBytes(kind);
        A.SetFull(kind, id, data);
        Oracle.SetFull(kind, id, data);
    }

    public void SetColour(ViewId quad, bool uniform = false)
    {
        byte[] c = RandomColours(uniform);
        A.SetColour(quad, c);
        Oracle.SetColour(quad, c);
    }

    public void Destroy(int kind, ViewId id, bool lazy = false)
    {
        A.Destroy(kind, id, lazy);
        Oracle.Destroy(kind, id);
    }

    public bool Emit(int kind = 7)
    {
        byte[] payload = BitConverter.GetBytes(++_eventCounter);
        if (!A.Emit(kind, payload))
        {
            return false;
        }
        Oracle.Emit(kind, payload);
        return true;
    }

    public ViewPublishResult Publish()
    {
        Oracle.Record(Writer.NextSeq, A.Tick + 1);
        return A.Publish();
    }

    /// <summary>Reader update; on a commit the drawable must equal the oracle state at that publication.</summary>
    public ViewApplyStatus Apply()
    {
        long before = Reader.CommittedSeq;
        ViewApplyStatus st = Reader.Update(Sink);
        if (Reader.CommittedSeq != before)
        {
            AssertDrawableIs(Reader.CommittedSeq);
        }
        return st;
    }

    public void AssertDrawableIs(long seq) => Assert.Null(ViewOracle.Diff(D, Oracle.At(seq)));

    /// <summary>Dispatched events must equal the oracle's emitted events in order (exactly once), up to its count.</summary>
    public void AssertEventsExactlyOnce(bool all = true)
    {
        List<(long Cs, int Kind, byte[] Payload)> got = Sink.Dispatched;
        if (all)
        {
            Assert.Equal(Oracle.Events.Count, got.Count);
        }
        Assert.True(got.Count <= Oracle.Events.Count);
        for (int i = 0; i < got.Count; i++)
        {
            Assert.Equal(Oracle.Events[i].Kind, got[i].Kind);
            Assert.Equal(Oracle.Events[i].Payload, got[i].Payload);
            if (i > 0)
            {
                Assert.True(got[i].Cs > got[i - 1].Cs);
            }
        }
    }

    public static ViewValueRec? FindValue(ViewPublication pub, int kind, ViewId id)
    {
        for (int i = 0; i < pub.ValueCount; i++)
        {
            if (pub.Values[i].Kind == kind && pub.Values[i].Id == id)
            {
                return pub.Values[i];
            }
        }
        return null;
    }
}
