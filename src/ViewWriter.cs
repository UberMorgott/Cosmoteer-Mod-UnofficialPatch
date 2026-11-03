using System;

namespace UnofficialPatch;

internal enum ViewWriterMode
{
    /// <summary>M1 (spec §R10 F3): no chunk caps, so every publication is complete except while
    /// <see cref="ViewWriter.UnprovenGone"/> holds one back (fail closed); the payload cap is soft and exceeding it counts
    /// <see cref="ViewWriter.Overflow"/> (gates G4/G6 fail on any overflow).</summary>
    FailClosed,

    /// <summary>M2 / protocol tests only: publications are dependency-closed chunks until everything fits.</summary>
    Chunked,
}

internal readonly struct ViewPublishResult(long seq, bool complete, bool skippedPrevious, int lifeOps, int valueOps, int events, int bytes)
{
    public long Seq { get; } = seq;
    public bool Complete { get; } = complete;
    public bool SkippedPrevious { get; } = skippedPrevious;
    public int LifeOps { get; } = lifeOps;
    public int ValueOps { get; } = valueOps;
    public int Events { get; } = events;
    public int Bytes { get; } = bytes;
}

/// <summary>
/// Sim-side half of the render-view protocol (decoupling spec §R2 as amended by §R10). Single-threaded: the sim thread
/// owns it. Not wired into the game yet (S1 infrastructure).
/// <list type="bullet">
/// <item>Every mutation gets <c>cs = ++ChangeSeq</c>.</item>
/// <item><b>Lifecycle log</b> (F2): append-only, never reordered; Create/Destroy per kind. Trimmed only up to the
/// lifecycle cut of a publication the reader acknowledged.</item>
/// <item><b>Value dirty list</b>: one intrusive doubly linked list over all kinds, ordered by lastCs (rewrite = move to
/// tail). Values are read from the <see cref="IViewSource"/> at publish time.</item>
/// <item><b>Acknowledged full base</b> (F1): per entry and generation, <c>AckedFullCs</c> = the full-value cs the reader
/// acknowledged for exactly this (slot, gen). A colour op is emitted only when <c>FullCs &lt;= AckedFullCs</c>; no other
/// entry's ack and no global cut can authorise it.</item>
/// <item><b>Acks</b>: the reader stores the seq of the last successfully applied publication. The writer keeps the
/// records of what each of the three ring buffers carried (writer-private); at the next publish it applies the record
/// set whose seq equals the ack. A buffer's records are only rewritten after the ack check of the publish that refills
/// it, and the reader acks before it releases the buffer, so the acked records are always intact.</item>
/// <item><b>Event log</b> (R4): effect/audio commands since the reader's dispatch cursor, carried by complete
/// publications; hard cap <see cref="MaxEvents"/>, overflow = protocol error (<see cref="EventOverflow"/>).</item>
/// </list>
/// </summary>
internal sealed class ViewWriter
{
    private const int LifeRecBytes = 32;
    private const int ValueRecBytes = 24;
    private const int EventRecBytes = 16;

    private struct Entry
    {
        public long CreateCs;
        public long LastCs;
        public long FullCs;
        public long AckedFullCs;
        public uint Gen;
        public int Prev;
        public int Next;
        public bool Alive;
        public bool Linked;

        // Cascade provenance (handles stored +1, 0 = none): parent link, child list, and the cs of the logged ancestor
        // Destroy that doomed this object (0 = none). Only such an object may vanish from the source without its own Destroy.
        public int ParentPlus1;
        public int FirstChildPlus1;
        public int NextSiblingPlus1;
        public int PrevSiblingPlus1;
        public long CascadeCs;
        public bool Unproven;
    }

    private struct AckRec
    {
        public int Handle;
        public uint Gen;
        public long Cs;
        public long FullCs;
        public bool Full;
    }

    private readonly ViewRing<ViewPublication> _ring;
    private readonly IViewPayloadModel _model;
    private readonly Entry[][] _entries;
    private readonly int[] _fullSize;
    private readonly int[] _colourMax;
    private int _dirtyHead = -1;
    private int _dirtyTail = -1;

    private ViewLifeRec[] _life = new ViewLifeRec[256];
    private int _lifeHead;
    private int _lifeCount;
    private long _ackLifeCs;

    private ViewEventRec[] _events = new ViewEventRec[256];
    private int _eventHead;
    private int _eventCount;
    private byte[] _eventData = new byte[4096];
    private int _eventDataLength;

    private readonly AckRec[][] _records = [new AckRec[256], new AckRec[256], new AckRec[256]];
    private readonly int[] _recordCount = new int[3];
    private readonly long[] _recordSeq = new long[3];
    private readonly long[] _recordLifeCut = new long[3];
    private long _processedAck;
    private long _seq;

    public ViewWriter(ViewRing<ViewPublication> ring, IViewPayloadModel model, ViewWriterMode mode)
    {
        if (model.KindCount is <= 0 or > ViewHandle.MaxKinds)
        {
            throw new ArgumentOutOfRangeException(nameof(model), "kind count must be 1..32");
        }
        _ring = ring;
        _model = model;
        Mode = mode;
        _entries = new Entry[model.KindCount][];
        _fullSize = new int[model.KindCount];
        _colourMax = new int[model.KindCount];
        for (int k = 0; k < model.KindCount; k++)
        {
            _entries[k] = new Entry[16];
            _fullSize[k] = model.FullSize(k);
            _colourMax[k] = model.ColourMaxSize(k);
        }
    }

    public ViewWriterMode Mode { get; set; }

    /// <summary>Soft cap in FailClosed mode (counts <see cref="Overflow"/>), hard per-chunk cap in Chunked mode.</summary>
    public int MaxPayloadBytes { get; set; } = 16 << 20;

    /// <summary>Chunked mode: max items (life + value ops) per chunk. A chunk always carries at least one item.</summary>
    public int ChunkItemCap { get; set; } = int.MaxValue;

    /// <summary>Chunked mode: max lifecycle ops per chunk (the lifecycle cut may stop before the value walk does).</summary>
    public int ChunkLifeCap { get; set; } = int.MaxValue;

    /// <summary>Hard cap on undispatched event commands; an emit beyond it is refused and counted (protocol error).</summary>
    public int MaxEvents { get; set; } = 1 << 20;

    public long ChangeSeq { get; private set; }
    public long NextSeq => _seq + 1;
    public long AckLifeCs => _ackLifeCs;
    public long ProcessedAck => _processedAck;
    public int Overflow { get; private set; }
    public int EventOverflow { get; private set; }
    public int DroppedGone { get; private set; }

    /// <summary>Protocol error (fail closed): an object vanished from the source (or its slot was reused) without a
    /// logged Destroy of itself or an ancestor. Its entry stays dirty and no publication is complete while it lasts.</summary>
    public int UnprovenGone { get; private set; }
    public int Skipped { get; private set; }
    public int PendingLifeOps => _lifeCount - _lifeHead;
    public int PendingEvents => _eventCount - _eventHead;

    // ---- mutations (sim side) ----

    /// <summary>Records a Create; the object's value is dirty (full) from this cs on. Parents must be created first.</summary>
    public long Create(int kind, ViewId id, int parentKind, ViewId parent)
    {
        if (parentKind >= 0)
        {
            EntryFor(parentKind, parent.Slot); // grow first: the ref below must not be invalidated
        }
        int h = ViewHandle.Make(kind, id.Slot);
        ref Entry e = ref EntryFor(kind, id.Slot);
        if (e.Alive && e.Gen == id.Gen)
        {
            throw new InvalidOperationException($"view object {kind}/{id} created twice");
        }
        if (e.Alive && e.CascadeCs == 0 && !e.Unproven)
        {
            // The previous generation was freed without any logged Destroy: the reader would never drop it.
            UnprovenGone++;
        }
        if (e.Linked)
        {
            Unlink(ref e);
        }
        DetachFromParent(ref e);
        DetachChildren(ref e);
        e.CascadeCs = 0;
        e.Unproven = false;
        long cs = ++ChangeSeq;
        AppendLife(new ViewLifeRec { Cs = cs, Id = id, Parent = parent, Kind = (byte)kind, ParentKind = (sbyte)parentKind, Op = ViewLifeOp.Create });
        e.Gen = id.Gen;
        e.Alive = true;
        e.CreateCs = cs;
        e.FullCs = cs;
        e.AckedFullCs = 0;
        e.LastCs = cs;
        if (parentKind >= 0)
        {
            ref Entry p = ref _entries[parentKind][parent.Slot];
            if (p.Alive && p.Gen == parent.Gen)
            {
                e.ParentPlus1 = ViewHandle.Make(parentKind, parent.Slot) + 1;
                e.PrevSiblingPlus1 = 0;
                e.NextSiblingPlus1 = p.FirstChildPlus1;
                if (p.FirstChildPlus1 != 0)
                {
                    At(p.FirstChildPlus1 - 1).PrevSiblingPlus1 = h + 1;
                }
                p.FirstChildPlus1 = h + 1;
            }
        }
        if (_fullSize[kind] > 0)
        {
            LinkTail(h, ref e);
        }
        return cs;
    }

    public long Destroy(int kind, ViewId id)
    {
        ref Entry e = ref EntryFor(kind, id.Slot);
        long cs = ++ChangeSeq;
        if (e.Alive && e.Gen == id.Gen)
        {
            if (e.Linked)
            {
                Unlink(ref e);
            }
            e.Alive = false;
            MarkCascade(e.FirstChildPlus1, cs);
            DetachFromParent(ref e);
            DetachChildren(ref e);
        }
        AppendLife(new ViewLifeRec { Cs = cs, Id = id, Kind = (byte)kind, ParentKind = -1, Op = ViewLifeOp.Destroy });
        return cs;
    }

    /// <summary>A non-colour (or unclassified) value write: the next op for this object is a full op until acked.</summary>
    public long SetValue(int kind, ViewId id)
    {
        ref Entry e = ref Live(kind, id);
        long cs = ++ChangeSeq;
        e.FullCs = cs;
        Touch(kind, id.Slot, ref e, cs);
        return cs;
    }

    /// <summary>A write that changed only colour bytes (the caller's byte-compare classification, spec §R1).</summary>
    public long SetColour(int kind, ViewId id)
    {
        if (_colourMax[kind] == 0)
        {
            throw new InvalidOperationException($"kind {kind} has no colour ops");
        }
        ref Entry e = ref Live(kind, id);
        long cs = ++ChangeSeq;
        Touch(kind, id.Slot, ref e, cs);
        return cs;
    }

    /// <summary>Appends an effect/audio command. Returns false (and counts a protocol error) at the hard cap.</summary>
    public bool Emit(int kind, ReadOnlySpan<byte> payload)
    {
        if (_eventCount - _eventHead >= MaxEvents)
        {
            EventOverflow++;
            return false;
        }
        long cs = ++ChangeSeq;
        if (_eventCount == _events.Length)
        {
            CompactEvents();
            if (_eventCount == _events.Length)
            {
                Array.Resize(ref _events, _events.Length * 2);
            }
        }
        int need = _eventDataLength + payload.Length;
        if (need > _eventData.Length)
        {
            CompactEvents();
            need = _eventDataLength + payload.Length;
            if (need > _eventData.Length)
            {
                Array.Resize(ref _eventData, Math.Max(need, _eventData.Length * 2));
            }
        }
        payload.CopyTo(new Span<byte>(_eventData, _eventDataLength, payload.Length));
        _events[_eventCount++] = new ViewEventRec { Cs = cs, Kind = kind, Offset = _eventDataLength, Length = payload.Length };
        _eventDataLength += payload.Length;
        return true;
    }

    // ---- publication ----

    public ViewPublishResult Publish(IViewSource source)
    {
        ProcessAcks();

        int b = _ring.BackIndex;
        ViewPublication pub = _ring.Back;
        long seq = ++_seq;
        pub.Reset(seq);
        _recordSeq[b] = seq;
        _recordCount[b] = 0;
        bool chunked = Mode == ViewWriterMode.Chunked;
        int items = 0;
        int bytes = 0;

        // 1. Lifecycle prefix after the acknowledged cut.
        bool lifeAll = true;
        long lifeCut = _ackLifeCs;
        int lifeOps = 0;
        for (int i = _lifeHead; i < _lifeCount; i++)
        {
            if (chunked && items > 0 && (lifeOps >= ChunkLifeCap || items >= ChunkItemCap || bytes + LifeRecBytes > MaxPayloadBytes))
            {
                lifeAll = false;
                break;
            }
            pub.AddLife(_life[i]);
            lifeCut = _life[i].Cs;
            items++;
            lifeOps++;
            bytes += LifeRecBytes;
        }
        if (lifeAll)
        {
            lifeCut = ChangeSeq;
        }

        // 2. Values in lastCs order, only for objects whose Create is inside the lifecycle cut (or acked before).
        bool valuesAll = true;
        int h = _dirtyHead;
        while (h != -1)
        {
            int kind = ViewHandle.Kind(h);
            uint slot = ViewHandle.Slot(h);
            ref Entry e = ref _entries[kind][slot];
            int next = e.Next;
            if (e.CreateCs > lifeCut)
            {
                valuesAll = false;
                h = next;
                continue;
            }
            bool full = _colourMax[kind] == 0 || e.FullCs > e.AckedFullCs;
            int size = full ? _fullSize[kind] : _colourMax[kind];
            if (chunked && items > 0 && (items >= ChunkItemCap || bytes + size + ValueRecBytes > MaxPayloadBytes))
            {
                valuesAll = false;
                break;
            }
            var id = new ViewId(slot, e.Gen);
            Span<byte> dst = pub.Reserve(size, out int offset);
            int length = full ? (source.TryWriteFull(kind, id, dst) ? size : -1) : source.TryWriteColour(kind, id, dst);
            if (length < 0)
            {
                pub.Trim(size);
                if (e.CascadeCs != 0 && e.CascadeCs <= lifeCut)
                {
                    // Freed without its own Destroy, but an ancestor's logged Destroy is in this cut or acked: the
                    // reader's cascade removes it, so nothing is left to publish.
                    Unlink(ref e);
                    e.Alive = false;
                    DetachFromParent(ref e);
                    DetachChildren(ref e);
                    DroppedGone++;
                }
                else
                {
                    // No proven cascade inside this cut: keep it dirty and do not claim completeness (fail closed).
                    if (e.CascadeCs == 0 && !e.Unproven)
                    {
                        e.Unproven = true;
                        UnprovenGone++;
                    }
                    valuesAll = false;
                }
                h = next;
                continue;
            }
            if (!full && !_model.IsValidColourLength(kind, length))
            {
                throw new InvalidOperationException($"source wrote an invalid colour op ({length} B) for kind {kind}");
            }
            pub.Trim(size - length);
            pub.AddValue(new ViewValueRec { Cs = e.LastCs, Id = id, Offset = offset, Length = length, Kind = (byte)kind, Op = full ? ViewValueOp.Full : ViewValueOp.Colour });
            AddRecord(b, new AckRec { Handle = h, Gen = e.Gen, Cs = e.LastCs, FullCs = full ? e.FullCs : 0, Full = full });
            items++;
            bytes += length + ValueRecBytes;
            h = next;
        }

        bool complete = lifeAll && valuesAll;
        int events = 0;
        if (complete)
        {
            // 3. Event commands since the reader's dispatch cursor, in cs order.
            long dispatched = _ring.ReadDispatched();
            for (int i = _eventHead; i < _eventCount; i++)
            {
                ref ViewEventRec ev = ref _events[i];
                if (ev.Cs <= dispatched)
                {
                    continue;
                }
                Span<byte> dst = pub.Reserve(ev.Length, out int offset);
                new ReadOnlySpan<byte>(_eventData, ev.Offset, ev.Length).CopyTo(dst);
                pub.AddEvent(new ViewEventRec { Cs = ev.Cs, Kind = ev.Kind, Offset = offset, Length = ev.Length });
                bytes += ev.Length + EventRecBytes;
                events++;
            }

            // 4. Snapshot of the completed tick.
            int snap = source.SnapshotLength;
            Span<byte> snapDst = pub.Reserve(snap, out int snapOffset);
            source.WriteSnapshot(snapDst);
            pub.SnapshotOffset = snapOffset;
            pub.SnapshotLength = snap;
            bytes += snap;
        }

        pub.Complete = complete;
        pub.LifeCut = lifeCut;
        pub.ChangeSeq = ChangeSeq;
        pub.PayloadBytes = bytes;
        if (Mode == ViewWriterMode.FailClosed && bytes > MaxPayloadBytes)
        {
            pub.Overflow = true;
            Overflow++;
        }
        _recordLifeCut[b] = lifeCut;

        bool skipped = _ring.Publish();
        if (skipped)
        {
            Skipped++;
        }
        return new ViewPublishResult(seq, complete, skipped, pub.LifeCount, pub.ValueCount, events, bytes);
    }

    // ---- diagnostics (tests, ViewStats) ----

    public bool IsDirty(int kind, ViewId id)
    {
        ref Entry e = ref EntryFor(kind, id.Slot);
        return e.Alive && e.Gen == id.Gen && e.Linked;
    }

    /// <summary>F1: the next op for this object must be full (sticky until the reader acks a full op of this generation).</summary>
    public bool NeedsFull(int kind, ViewId id)
    {
        ref Entry e = ref EntryFor(kind, id.Slot);
        return e.Alive && e.Gen == id.Gen && e.FullCs > e.AckedFullCs;
    }

    public int DirtyCount
    {
        get
        {
            int n = 0;
            for (int h = _dirtyHead; h != -1; h = _entries[ViewHandle.Kind(h)][ViewHandle.Slot(h)].Next)
            {
                n++;
            }
            return n;
        }
    }

    // ---- internals ----

    private void ProcessAcks()
    {
        long ack = _ring.ReadAck();
        if (ack > _processedAck)
        {
            for (int b = 0; b < 3; b++)
            {
                if (_recordSeq[b] != ack)
                {
                    continue;
                }
                AckRec[] recs = _records[b];
                for (int i = 0, n = _recordCount[b]; i < n; i++)
                {
                    ref AckRec r = ref recs[i];
                    ref Entry e = ref _entries[ViewHandle.Kind(r.Handle)][ViewHandle.Slot(r.Handle)];
                    if (!e.Alive || e.Gen != r.Gen)
                    {
                        continue;
                    }
                    if (r.Full && r.FullCs > e.AckedFullCs)
                    {
                        e.AckedFullCs = r.FullCs;
                    }
                    if (e.Linked && e.LastCs == r.Cs)
                    {
                        Unlink(ref e);
                    }
                }
                if (_recordLifeCut[b] > _ackLifeCs)
                {
                    _ackLifeCs = _recordLifeCut[b];
                }
                break;
            }
            // An ack whose records were not found (cannot happen under the lease rules) acks nothing: conservative.
            _processedAck = ack;
        }

        while (_lifeHead < _lifeCount && _life[_lifeHead].Cs <= _ackLifeCs)
        {
            _lifeHead++;
        }
        if (_lifeHead == _lifeCount)
        {
            _lifeHead = _lifeCount = 0;
        }

        long dispatched = _ring.ReadDispatched();
        while (_eventHead < _eventCount && _events[_eventHead].Cs <= dispatched)
        {
            _eventHead++;
        }
        if (_eventHead == _eventCount)
        {
            _eventHead = _eventCount = _eventDataLength = 0;
        }
    }

    private void AppendLife(in ViewLifeRec rec)
    {
        if (_lifeCount == _life.Length)
        {
            if (_lifeHead > 0)
            {
                Array.Copy(_life, _lifeHead, _life, 0, _lifeCount - _lifeHead);
                _lifeCount -= _lifeHead;
                _lifeHead = 0;
            }
            if (_lifeCount == _life.Length)
            {
                Array.Resize(ref _life, _life.Length * 2);
            }
        }
        _life[_lifeCount++] = rec;
    }

    private void CompactEvents()
    {
        if (_eventHead == 0)
        {
            return;
        }
        int n = _eventCount - _eventHead;
        int dataStart = n > 0 ? _events[_eventHead].Offset : _eventDataLength;
        int dataLength = _eventDataLength - dataStart;
        Array.Copy(_eventData, dataStart, _eventData, 0, dataLength);
        for (int i = 0; i < n; i++)
        {
            ViewEventRec ev = _events[_eventHead + i];
            ev.Offset -= dataStart;
            _events[i] = ev;
        }
        _eventHead = 0;
        _eventCount = n;
        _eventDataLength = dataLength;
    }

    private void AddRecord(int b, in AckRec rec)
    {
        AckRec[] recs = _records[b];
        if (_recordCount[b] == recs.Length)
        {
            Array.Resize(ref recs, recs.Length * 2);
            _records[b] = recs;
        }
        recs[_recordCount[b]++] = rec;
    }

    private ref Entry At(int handle) => ref _entries[ViewHandle.Kind(handle)][ViewHandle.Slot(handle)];

    /// <summary>Marks every descendant (child list starting at <paramref name="firstPlus1"/>) as doomed by the logged
    /// Destroy at <paramref name="cs"/> (the earliest such Destroy wins).</summary>
    private void MarkCascade(int firstPlus1, long cs)
    {
        for (int c = firstPlus1; c != 0;)
        {
            ref Entry ce = ref At(c - 1);
            if (ce.CascadeCs == 0)
            {
                ce.CascadeCs = cs;
            }
            MarkCascade(ce.FirstChildPlus1, cs);
            c = ce.NextSiblingPlus1;
        }
    }

    private void DetachFromParent(ref Entry e)
    {
        if (e.ParentPlus1 == 0)
        {
            return;
        }
        if (e.PrevSiblingPlus1 != 0)
        {
            At(e.PrevSiblingPlus1 - 1).NextSiblingPlus1 = e.NextSiblingPlus1;
        }
        else
        {
            At(e.ParentPlus1 - 1).FirstChildPlus1 = e.NextSiblingPlus1;
        }
        if (e.NextSiblingPlus1 != 0)
        {
            At(e.NextSiblingPlus1 - 1).PrevSiblingPlus1 = e.PrevSiblingPlus1;
        }
        e.ParentPlus1 = e.NextSiblingPlus1 = e.PrevSiblingPlus1 = 0;
    }

    private void DetachChildren(ref Entry e)
    {
        int c = e.FirstChildPlus1;
        e.FirstChildPlus1 = 0;
        while (c != 0)
        {
            ref Entry ce = ref At(c - 1);
            int next = ce.NextSiblingPlus1;
            ce.ParentPlus1 = ce.NextSiblingPlus1 = ce.PrevSiblingPlus1 = 0;
            c = next;
        }
    }

    private ref Entry EntryFor(int kind, uint slot)
    {
        if ((uint)kind >= (uint)_entries.Length || slot >= ViewSlotMap.MaxSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), $"view handle {kind}/{slot} out of range");
        }
        Entry[] arr = _entries[kind];
        if (slot >= arr.Length)
        {
            int size = arr.Length;
            while (size <= slot)
            {
                size *= 2;
            }
            Array.Resize(ref arr, size);
            _entries[kind] = arr;
        }
        return ref arr[slot];
    }

    private ref Entry Live(int kind, ViewId id)
    {
        ref Entry e = ref EntryFor(kind, id.Slot);
        if (!e.Alive || e.Gen != id.Gen)
        {
            throw new InvalidOperationException($"view object {kind}/{id} is not alive on the writer");
        }
        if (_fullSize[kind] == 0)
        {
            throw new InvalidOperationException($"kind {kind} has no values");
        }
        return ref e;
    }

    private void Touch(int kind, uint slot, ref Entry e, long cs)
    {
        int h = ViewHandle.Make(kind, slot);
        if (e.Linked)
        {
            Unlink(ref e);
        }
        e.LastCs = cs;
        LinkTail(h, ref e);
    }

    private void LinkTail(int h, ref Entry e)
    {
        e.Prev = _dirtyTail;
        e.Next = -1;
        e.Linked = true;
        if (_dirtyTail == -1)
        {
            _dirtyHead = h;
        }
        else
        {
            _entries[ViewHandle.Kind(_dirtyTail)][ViewHandle.Slot(_dirtyTail)].Next = h;
        }
        _dirtyTail = h;
    }

    private void Unlink(ref Entry e)
    {
        if (e.Prev == -1)
        {
            _dirtyHead = e.Next;
        }
        else
        {
            _entries[ViewHandle.Kind(e.Prev)][ViewHandle.Slot(e.Prev)].Next = e.Next;
        }
        if (e.Next == -1)
        {
            _dirtyTail = e.Prev;
        }
        else
        {
            _entries[ViewHandle.Kind(e.Next)][ViewHandle.Slot(e.Next)].Prev = e.Prev;
        }
        e.Prev = e.Next = -1;
        e.Linked = false;
    }
}
