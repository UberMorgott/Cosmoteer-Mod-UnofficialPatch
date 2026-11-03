using System;

namespace UnofficialPatch;

internal enum ViewApplyStatus
{
    /// <summary>No new publication and nothing left to retry.</summary>
    Idle,

    /// <summary>A chunk was staged and acked; the drawable is unchanged.</summary>
    Staged,

    /// <summary>A complete publication was committed to the drawable, acked, and all its events dispatched.</summary>
    Committed,

    /// <summary>Validation of the publication failed: nothing applied, nothing acked; retried on the next update.</summary>
    Failed,

    /// <summary>State committed and acked, but an event dispatch threw <see cref="ViewEventRetryableException"/> (no
    /// visible work); it and the remaining events are retried on the next update.</summary>
    EventFailed,

    /// <summary>An event failed terminally (uncertain exception or retries exhausted): counted, logged, never repeated;
    /// dispatch stops for this update and resumes after it on the next. A protocol error (fails M1 acceptance).</summary>
    EventTerminal,
}

/// <summary>
/// Render-side half of the view protocol (decoupling spec §R10 F3). Single-threaded: the reader thread owns it.
/// <list type="bullet">
/// <item><b>Drawable D</b> (<see cref="Drawable"/>): only ever the state at the publish point of one complete
/// publication. <b>Staging S</b>: a journal of acked chunk ops, never drawn.</item>
/// <item>A publication is validated in full before anything changes (a failure leaves D, S and the ack untouched).
/// A chunk is appended to S, then acked. A complete publication commits S + its own ops into D (apply cannot fail after
/// validation), swaps in its snapshot, then acks.</item>
/// <item><b>Ack after apply</b>: the ack (publication seq) is stored only after a successful stage/commit.</item>
/// <item><b>Events</b>: dispatched after the commit in cs order; the dispatch cursor advances after each successful
/// dispatch and is the event ack, so a retry (same or later publication) never repeats a dispatched command. Failure
/// contract: see <see cref="IViewEventSink"/> (retryable only on the explicit exception, everything else terminal).</item>
/// </list>
/// </summary>
internal sealed class ViewReader
{
    private readonly ViewRing<ViewPublication> _ring;
    private readonly IViewPayloadModel _model;
    private ViewPublication? _pub;
    private bool _stateDone = true;
    private bool _eventsDone = true;
    private long _lastSeq;
    private long _dispatchedCs;
    private long _retryCs;
    private int _attempts;

    // Staging S: journal of acked chunk ops in apply order (life before values per chunk).
    private ViewLifeRec[] _jLife = new ViewLifeRec[64];
    private int _jLifeCount;
    private ViewValueRec[] _jValues = new ViewValueRec[256];
    private int _jValueCount;
    private int[] _jChunkLifeEnd = new int[8];
    private int[] _jChunkValueEnd = new int[8];
    private int _jChunks;
    private byte[] _jData = new byte[4096];
    private int _jDataLength;

    public ViewReader(ViewRing<ViewPublication> ring, IViewPayloadModel model, ViewTable drawable)
    {
        _ring = ring;
        _model = model;
        Drawable = drawable;
    }

    public ViewTable Drawable { get; }
    // ReSharper disable UnusedAutoPropertyAccessor.Global -- getters read by the protocol tests (other project).
    public long CommittedSeq { get; private set; }
    public long AckedSeq { get; private set; }
    public long DispatchedCs => _dispatchedCs;
    public int Failures { get; private set; }
    public int EventFailures { get; private set; }

    /// <summary>Events that failed uncertainly or exhausted their retries (protocol errors; gate fails on any).</summary>
    public int TerminalEventFailures { get; private set; }
    public long LastTerminalCs { get; private set; }
    // ReSharper restore UnusedAutoPropertyAccessor.Global

    /// <summary>Total dispatch attempts per command for <see cref="ViewEventRetryableException"/> failures.</summary>
    public int MaxEventRetries { get; set; } = 3;
    public int SeqViolations { get; private set; }
    public int StagedChunks => _jChunks;

    /// <summary>The publication held under the read lease (valid until the next <see cref="Update"/>).</summary>
    public ViewPublication? Current => _pub;

    /// <summary>Test seam: validation throws when it reaches this op index (life ops, then values); -1 = off.</summary>
    internal int InjectFaultAtOp { get; set; } = -1;

    public ViewApplyStatus Update(IViewEventSink sink)
    {
        if (_ring.TryAcquire())
        {
            _pub = _ring.Front;
            if (_pub.Seq <= _lastSeq)
            {
                SeqViolations++;
            }
            _lastSeq = _pub.Seq;
            _stateDone = false;
            _eventsDone = false;
        }
        if (_pub == null || (_stateDone && _eventsDone))
        {
            return ViewApplyStatus.Idle;
        }
        ViewPublication pub = _pub;
        if (!_stateDone)
        {
            try
            {
                Validate(pub);
            }
            catch (Exception)
            {
                Failures++;
                return ViewApplyStatus.Failed;
            }
            if (!pub.Complete)
            {
                AppendJournal(pub);
                _stateDone = _eventsDone = true;
                Ack(pub.Seq);
                return ViewApplyStatus.Staged;
            }
            Commit(pub);
            _stateDone = true;
            CommittedSeq = pub.Seq;
            Ack(pub.Seq);
        }
        for (int i = 0; i < pub.EventCount; i++)
        {
            ref ViewEventRec ev = ref pub.Events[i];
            if (ev.Cs <= _dispatchedCs)
            {
                continue;
            }
            if (ev.Cs != _retryCs)
            {
                _retryCs = ev.Cs;
                _attempts = 0;
            }
            _attempts++;
            try
            {
                sink.Dispatch(ev.Cs, ev.Kind, pub.Bytes(ev.Offset, ev.Length));
            }
            catch (ViewEventRetryableException ex)
            {
                // The sink promised no visible work: retry later, but never forever.
                EventFailures++;
                if (_attempts < MaxEventRetries)
                {
                    return ViewApplyStatus.EventFailed;
                }
                Terminal(ev.Cs, ex);
                return ViewApplyStatus.EventTerminal;
            }
            catch (Exception ex)
            {
                // Uncertain: the command may have done visible work, so it is never dispatched again.
                EventFailures++;
                Terminal(ev.Cs, ex);
                return ViewApplyStatus.EventTerminal;
            }
            _dispatchedCs = ev.Cs;
            _ring.StoreDispatched(ev.Cs);
        }
        _eventsDone = true;
        return ViewApplyStatus.Committed;
    }

    /// <summary>Terminal event failure: counted, logged, recorded; the cursor moves past it so it is neither repeated
    /// (possible side effects) nor retried forever. A protocol error: M1 acceptance fails on any.</summary>
    private void Terminal(long cs, Exception ex)
    {
        TerminalEventFailures++;
        LastTerminalCs = cs;
        Log.Error("view-event-terminal", ex);
        _dispatchedCs = cs;
        _ring.StoreDispatched(cs);
    }

    private void Ack(long seq)
    {
        AckedSeq = seq;
        _ring.StoreAck(seq);
    }

    /// <summary>Checks every op without changing state; throws on a malformed publication (or an injected fault).</summary>
    private void Validate(ViewPublication pub)
    {
        int kinds = _model.KindCount;
        int op = 0;
        for (int i = 0; i < pub.LifeCount; i++, op++)
        {
            Fault(op);
            ref ViewLifeRec r = ref pub.Life[i];
            if (r.Kind >= kinds || r.ParentKind >= kinds || r.Id.Gen == 0 || r.Id.Slot >= ViewSlotMap.MaxSlots)
            {
                throw new InvalidOperationException($"bad life op {i} in publication {pub.Seq}");
            }
        }
        for (int i = 0; i < pub.ValueCount; i++, op++)
        {
            Fault(op);
            ref ViewValueRec r = ref pub.Values[i];
            if (r.Kind >= kinds || r.Offset < 0 || r.Offset + r.Length > pub.DataLength)
            {
                throw new InvalidOperationException($"bad value op {i} in publication {pub.Seq}");
            }
            bool ok = r.Op == ViewValueOp.Full ? r.Length == _model.FullSize(r.Kind) : r.Op == ViewValueOp.Colour && _model.IsValidColourLength(r.Kind, r.Length);
            if (!ok)
            {
                throw new InvalidOperationException($"bad value op size {r.Length} for kind {r.Kind} in publication {pub.Seq}");
            }
        }
        if (pub.Complete && pub.SnapshotOffset + pub.SnapshotLength > pub.DataLength)
        {
            throw new InvalidOperationException($"bad snapshot in publication {pub.Seq}");
        }
        // Pre-grow every table the ops touch so the commit itself cannot fail half-way (growth only, no state change).
        for (int i = 0; i < pub.LifeCount; i++)
        {
            Drawable.Ensure(pub.Life[i].Kind, pub.Life[i].Id.Slot);
        }
    }

    private void Fault(int op)
    {
        if (op == InjectFaultAtOp)
        {
            throw new InvalidOperationException("injected apply fault");
        }
    }

    private void AppendJournal(ViewPublication pub)
    {
        if (_jLifeCount + pub.LifeCount > _jLife.Length)
        {
            Array.Resize(ref _jLife, Math.Max(_jLifeCount + pub.LifeCount, _jLife.Length * 2));
        }
        Array.Copy(pub.Life, 0, _jLife, _jLifeCount, pub.LifeCount);
        _jLifeCount += pub.LifeCount;
        if (_jValueCount + pub.ValueCount > _jValues.Length)
        {
            Array.Resize(ref _jValues, Math.Max(_jValueCount + pub.ValueCount, _jValues.Length * 2));
        }
        for (int i = 0; i < pub.ValueCount; i++)
        {
            ViewValueRec r = pub.Values[i];
            if (_jDataLength + r.Length > _jData.Length)
            {
                Array.Resize(ref _jData, Math.Max(_jDataLength + r.Length, _jData.Length * 2));
            }
            Array.Copy(pub.Data, r.Offset, _jData, _jDataLength, r.Length);
            r.Offset = _jDataLength;
            _jDataLength += r.Length;
            _jValues[_jValueCount++] = r;
        }
        if (_jChunks == _jChunkLifeEnd.Length)
        {
            Array.Resize(ref _jChunkLifeEnd, _jChunks * 2);
            Array.Resize(ref _jChunkValueEnd, _jChunks * 2);
        }
        _jChunkLifeEnd[_jChunks] = _jLifeCount;
        _jChunkValueEnd[_jChunks] = _jValueCount;
        _jChunks++;
    }

    private void Commit(ViewPublication pub)
    {
        ViewTable d = Drawable;
        int life = 0;
        int value = 0;
        for (int c = 0; c < _jChunks; c++)
        {
            for (; life < _jChunkLifeEnd[c]; life++)
            {
                d.ApplyLife(_jLife[life]);
            }
            for (; value < _jChunkValueEnd[c]; value++)
            {
                ref ViewValueRec r = ref _jValues[value];
                d.ApplyValue(r, new ReadOnlySpan<byte>(_jData, r.Offset, r.Length));
            }
        }
        _jChunks = _jLifeCount = _jValueCount = _jDataLength = 0;
        for (int i = 0; i < pub.LifeCount; i++)
        {
            d.ApplyLife(pub.Life[i]);
        }
        for (int i = 0; i < pub.ValueCount; i++)
        {
            ref ViewValueRec r = ref pub.Values[i];
            d.ApplyValue(r, pub.Bytes(r.Offset, r.Length));
        }
        d.SetSnapshot(pub.Snapshot);
    }
}
