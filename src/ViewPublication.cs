using System;

namespace Performance;

/// <summary>
/// Payload model the view protocol is generic over (spec §R10 F5): object kinds are small ints (&lt; 32), each with a
/// fixed full-value size and an optional colour-delta size. S4/S5 plug in poses and quads; tests plug in a toy model.
/// </summary>
internal interface IViewPayloadModel
{
    int KindCount { get; }

    /// <summary>Bytes of a full value op for <paramref name="kind"/> (0 = the kind has lifecycle only, no values).</summary>
    int FullSize(int kind);

    /// <summary>Largest colour-only op for <paramref name="kind"/> (0 = no colour deltas; every change is a full op).</summary>
    int ColourMaxSize(int kind);

    /// <summary>Writes a colour op (length 1..<see cref="ColourMaxSize"/>) into a full value. Must not throw for a length
    /// the model accepts; lengths are validated before any state changes.</summary>
    void ApplyColour(int kind, Span<byte> full, ReadOnlySpan<byte> colour);

    bool IsValidColourLength(int kind, int length);
}

/// <summary>Sim-side authoritative store, read by the writer at publish time (spec §R2 "source of payload").</summary>
internal interface IViewSource
{
    /// <summary>Copies the object's full value into <paramref name="dst"/> (exactly FullSize bytes); false = object gone.</summary>
    bool TryWriteFull(int kind, ViewId id, Span<byte> dst);

    /// <summary>Copies the colour-only payload; returns the length written, or -1 when the object is gone.</summary>
    int TryWriteColour(int kind, ViewId id, Span<byte> dst);

    /// <summary>Per-tick snapshot block (poses/topology tables) carried by complete publications only.</summary>
    int SnapshotLength { get; }

    void WriteSnapshot(Span<byte> dst);
}

/// <summary>
/// Render-side consumer of effect/audio commands, called after the drawable commit, in change-seq order.
/// Failure contract (exactly-once):
/// <list type="bullet">
/// <item>Throw <see cref="ViewEventRetryableException"/> only when the command did no visible work (nothing created,
/// played or queued). The reader retries it, up to <see cref="ViewReader.MaxEventRetries"/> attempts in total.</item>
/// <item>Any other exception is <b>uncertain</b> (work may have happened): the reader never re-dispatches that command
/// (no duplicate effect/audio), counts it as terminal, logs it and stops dispatch for this update. Terminal failures
/// are protocol errors and fail M1 acceptance; they are never skipped silently.</item>
/// <item>A retryable command that exhausts its retries is poisoned: terminal as above (counted, logged, not retried
/// forever), so one bad command cannot stall the stream.</item>
/// </list>
/// </summary>
internal interface IViewEventSink
{
    void Dispatch(long cs, int kind, ReadOnlySpan<byte> payload);
}

/// <summary>Thrown by an <see cref="IViewEventSink"/> to promise that the failed dispatch had no visible effect.</summary>
internal sealed class ViewEventRetryableException(string message) : Exception(message);

internal enum ViewLifeOp : byte
{
    Create = 1,
    Destroy = 2,
}

internal enum ViewValueOp : byte
{
    Full = 1,
    Colour = 2,
}

/// <summary>Lifecycle log entry (spec §R10 F2). Append-only and never reordered on the writer side.</summary>
internal struct ViewLifeRec
{
    public long Cs;
    public ViewId Id;
    public ViewId Parent;
    public byte Kind;
    public sbyte ParentKind;
    public ViewLifeOp Op;
}

internal struct ViewValueRec
{
    public long Cs;
    public ViewId Id;
    public int Offset;
    public int Length;
    public byte Kind;
    public ViewValueOp Op;
}

internal struct ViewEventRec
{
    public long Cs;
    public int Kind;
    public int Offset;
    public int Length;
}

/// <summary>
/// One publication buffer of the <see cref="ViewRing{T}"/> (spec §R2, §R10 F3). Grow-only arrays reused across
/// publications; nothing allocates once warm.
/// <list type="bullet">
/// <item><see cref="Complete"/>: the cuts reach the writer's change seq at publish time; carries the snapshot and the
/// event commands. Otherwise a chunk: a dependency-closed cut prefix, no snapshot, no events.</item>
/// <item>Life ops are a cs prefix of the writer's lifecycle log; value ops follow in lastCs order, only for objects whose
/// Create is included or already acknowledged.</item>
/// </list>
/// </summary>
internal sealed class ViewPublication
{
    public long Seq;
    public bool Complete;
    public long LifeCut;
    public long ChangeSeq;
    public bool Overflow;
    public int PayloadBytes;

    public ViewLifeRec[] Life = new ViewLifeRec[64];
    public int LifeCount;
    public ViewValueRec[] Values = new ViewValueRec[256];
    public int ValueCount;
    public ViewEventRec[] Events = new ViewEventRec[64];
    public int EventCount;
    public byte[] Data = new byte[4096];
    public int DataLength;
    public int SnapshotOffset;
    public int SnapshotLength;

    public ReadOnlySpan<byte> Bytes(int offset, int length) => new(Data, offset, length);
    public ReadOnlySpan<byte> Snapshot => new(Data, SnapshotOffset, SnapshotLength);

    internal void Reset(long seq)
    {
        Seq = seq;
        Complete = false;
        LifeCut = 0;
        ChangeSeq = 0;
        Overflow = false;
        PayloadBytes = 0;
        LifeCount = 0;
        ValueCount = 0;
        EventCount = 0;
        DataLength = 0;
        SnapshotOffset = 0;
        SnapshotLength = 0;
    }

    internal Span<byte> Reserve(int length, out int offset)
    {
        offset = DataLength;
        int need = DataLength + length;
        if (need > Data.Length)
        {
            Array.Resize(ref Data, Math.Max(need, Data.Length * 2));
        }
        DataLength = need;
        return new Span<byte>(Data, offset, length);
    }

    /// <summary>Drops the tail of the last reservation (the source wrote fewer bytes, or the object was gone).</summary>
    internal void Trim(int bytes) => DataLength -= bytes;

    internal void AddLife(in ViewLifeRec rec)
    {
        if (LifeCount == Life.Length)
        {
            Array.Resize(ref Life, Life.Length * 2);
        }
        Life[LifeCount++] = rec;
    }

    internal void AddValue(in ViewValueRec rec)
    {
        if (ValueCount == Values.Length)
        {
            Array.Resize(ref Values, Values.Length * 2);
        }
        Values[ValueCount++] = rec;
    }

    internal void AddEvent(in ViewEventRec rec)
    {
        if (EventCount == Events.Length)
        {
            Array.Resize(ref Events, Events.Length * 2);
        }
        Events[EventCount++] = rec;
    }
}

/// <summary>Kind and slot packed into one int (5 kind bits, 26 slot bits) for intrusive lists.</summary>
internal static class ViewHandle
{
    internal const int SlotBits = 26;
    internal const int SlotMask = (1 << SlotBits) - 1;
    internal const int MaxKinds = 32;

    internal static int Make(int kind, uint slot) => (kind << SlotBits) | (int)slot;
    internal static int Kind(int handle) => handle >>> SlotBits;
    internal static uint Slot(int handle) => (uint)(handle & SlotMask);
}
