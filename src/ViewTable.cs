using System;

namespace UnofficialPatch;

/// <summary>
/// CPU-side render tables keyed by (kind, slot): the reader's drawable D for kinds that need no GPU mirror (poses,
/// topology) and the reference drawable for the protocol tests (spec §R10 F3). S5 quads plug a GPU-backed drawable in
/// with the same apply semantics.
/// <list type="bullet">
/// <item>Apply is idempotent and generation-monotone, so a publication built before the writer saw an ack can resend ops
/// the reader already applied: Create(g) applies only when g is newer than the slot's last generation; Destroy and value
/// ops apply only to the alive object of exactly that generation.</item>
/// <item>Destroy cascades over intrusive child lists (a ship destroy kills its layers and their quads).</item>
/// <item>Value ops for a dead (slot, gen) are ignored and counted in <see cref="StaleValueOps"/>.</item>
/// <item>Apply never throws once capacity is ensured: inputs are validated before any state changes.</item>
/// </list>
/// </summary>
internal sealed class ViewTable
{
    private sealed class KindTable(int fullSize)
    {
        public readonly int FullSize = fullSize;
        public uint[] Gen = new uint[16];
        public bool[] Alive = new bool[16];
        public int[] Parent = new int[16];
        public uint[] ParentGen = new uint[16];
        public int[] FirstChild = new int[16];
        public int[] NextSibling = new int[16];
        public int[] PrevSibling = new int[16];
        public byte[] Data = new byte[16 * Math.Max(fullSize, 1)];
        public int Capacity = 16;

        public void Ensure(uint slot)
        {
            if (slot < Capacity)
            {
                return;
            }
            int size = Capacity;
            while (size <= slot)
            {
                size *= 2;
            }
            Array.Resize(ref Gen, size);
            Array.Resize(ref Alive, size);
            Array.Resize(ref Parent, size);
            Array.Resize(ref ParentGen, size);
            Array.Resize(ref FirstChild, size);
            Array.Resize(ref NextSibling, size);
            Array.Resize(ref PrevSibling, size);
            Array.Resize(ref Data, size * Math.Max(FullSize, 1));
            Capacity = size;
        }
    }

    private readonly IViewPayloadModel _model;
    private readonly KindTable[] _kinds;
    private byte[] _snapshot = new byte[64];

    public ViewTable(IViewPayloadModel model)
    {
        _model = model;
        _kinds = new KindTable[model.KindCount];
        for (int k = 0; k < _kinds.Length; k++)
        {
            _kinds[k] = new KindTable(model.FullSize(k));
        }
    }

    public int StaleValueOps { get; private set; }
    public int IgnoredLifeOps { get; private set; }
    public int OrphanCreates { get; private set; }
    public int ImplicitKills { get; private set; }
    public int SnapshotLength { get; private set; }
    public ReadOnlySpan<byte> Snapshot => new(_snapshot, 0, SnapshotLength);
    public int KindCount => _kinds.Length;

    public int SlotCapacity(int kind) => _kinds[kind].Capacity;

    public bool TryResolve(int kind, ViewId id)
    {
        KindTable t = _kinds[kind];
        return id.Gen != 0 && id.Slot < t.Capacity && t.Alive[id.Slot] && t.Gen[id.Slot] == id.Gen;
    }

    public bool IsAlive(int kind, uint slot, out ViewId id)
    {
        KindTable t = _kinds[kind];
        if (slot < t.Capacity && t.Alive[slot])
        {
            id = new ViewId(slot, t.Gen[slot]);
            return true;
        }
        id = default;
        return false;
    }

    public ReadOnlySpan<byte> DataOf(int kind, uint slot)
    {
        KindTable t = _kinds[kind];
        return new ReadOnlySpan<byte>(t.Data, (int)slot * t.FullSize, t.FullSize);
    }

    /// <summary>Parent of a live object as (kind, id); kind -1 = no parent.</summary>
    public int ParentOf(int kind, uint slot, out ViewId parent)
    {
        KindTable t = _kinds[kind];
        int h = t.Parent[slot];
        if (h < 0)
        {
            parent = default;
            return -1;
        }
        parent = new ViewId(ViewHandle.Slot(h), t.ParentGen[slot]);
        return ViewHandle.Kind(h);
    }

    // ---- apply (reader-owned) ----

    internal void Ensure(int kind, uint slot) => _kinds[kind].Ensure(slot);

    internal void ApplyLife(in ViewLifeRec op)
    {
        KindTable t = _kinds[op.Kind];
        uint slot = op.Id.Slot;
        t.Ensure(slot);
        if (op.Op == ViewLifeOp.Create)
        {
            if (op.Id.Gen <= t.Gen[slot])
            {
                IgnoredLifeOps++;
                return;
            }
            if (t.Alive[slot])
            {
                // An older generation that was never destroyed (the writer counted it as UnprovenGone): unlink it
                // properly before the slot is reused, and count it.
                ImplicitKills++;
                Kill(op.Kind, slot);
            }
            t.Gen[slot] = op.Id.Gen;
            if (op.ParentKind >= 0 && !TryResolve(op.ParentKind, op.Parent))
            {
                // Parent already gone (cannot happen for a dependency-closed stream; counted, the child stays dead).
                t.Alive[slot] = false;
                OrphanCreates++;
                return;
            }
            t.Alive[slot] = true;
            t.FirstChild[slot] = -1;
            t.Data.AsSpan((int)slot * t.FullSize, t.FullSize).Clear();
            int self = ViewHandle.Make(op.Kind, slot);
            if (op.ParentKind >= 0)
            {
                KindTable pt = _kinds[op.ParentKind];
                int ph = ViewHandle.Make(op.ParentKind, op.Parent.Slot);
                t.Parent[slot] = ph;
                t.ParentGen[slot] = op.Parent.Gen;
                int first = pt.FirstChild[op.Parent.Slot];
                t.PrevSibling[slot] = -1;
                t.NextSibling[slot] = first;
                if (first >= 0)
                {
                    _kinds[ViewHandle.Kind(first)].PrevSibling[ViewHandle.Slot(first)] = self;
                }
                pt.FirstChild[op.Parent.Slot] = self;
            }
            else
            {
                t.Parent[slot] = -1;
                t.ParentGen[slot] = 0;
                t.PrevSibling[slot] = -1;
                t.NextSibling[slot] = -1;
            }
            return;
        }
        if (!t.Alive[slot] || t.Gen[slot] != op.Id.Gen)
        {
            IgnoredLifeOps++;
            return;
        }
        Kill(op.Kind, slot);
    }

    internal void ApplyValue(in ViewValueRec op, ReadOnlySpan<byte> payload)
    {
        KindTable t = _kinds[op.Kind];
        uint slot = op.Id.Slot;
        if (slot >= t.Capacity || !t.Alive[slot] || t.Gen[slot] != op.Id.Gen)
        {
            StaleValueOps++;
            return;
        }
        Span<byte> data = t.Data.AsSpan((int)slot * t.FullSize, t.FullSize);
        if (op.Op == ViewValueOp.Full)
        {
            payload.CopyTo(data);
        }
        else
        {
            _model.ApplyColour(op.Kind, data, payload);
        }
    }

    internal void SetSnapshot(ReadOnlySpan<byte> snapshot)
    {
        if (snapshot.Length > _snapshot.Length)
        {
            Array.Resize(ref _snapshot, Math.Max(snapshot.Length, _snapshot.Length * 2));
        }
        snapshot.CopyTo(_snapshot);
        SnapshotLength = snapshot.Length;
    }

    private void Kill(int kind, uint slot)
    {
        KindTable t = _kinds[kind];
        // Children first (each kill unlinks itself from this list).
        while (t.FirstChild[slot] >= 0)
        {
            int c = t.FirstChild[slot];
            Kill(ViewHandle.Kind(c), ViewHandle.Slot(c));
        }
        int ph = t.Parent[slot];
        if (ph >= 0)
        {
            int prev = t.PrevSibling[slot];
            int next = t.NextSibling[slot];
            if (prev >= 0)
            {
                _kinds[ViewHandle.Kind(prev)].NextSibling[ViewHandle.Slot(prev)] = next;
            }
            else
            {
                _kinds[ViewHandle.Kind(ph)].FirstChild[ViewHandle.Slot(ph)] = next;
            }
            if (next >= 0)
            {
                _kinds[ViewHandle.Kind(next)].PrevSibling[ViewHandle.Slot(next)] = prev;
            }
        }
        t.Alive[slot] = false;
        t.Parent[slot] = -1;
    }
}
