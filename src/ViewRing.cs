using System.Threading;

namespace UnofficialPatch;

/// <summary>
/// Classic triple buffer with one atomic state word (decoupling spec §R2 "Ring linearisation", §R10 lease rules).
/// <list type="bullet">
/// <item><c>state</c>: bits 0-1 = index of the shared middle buffer, bit 2 = fresh. The writer privately owns
/// <c>back</c>, the reader privately owns <c>front</c>; {back, middle, front} is always a permutation of {0,1,2}.</item>
/// <item><see cref="Publish"/> and <see cref="TryAcquire"/> each swap one private index with the shared one in a single
/// <see cref="Interlocked.Exchange(ref int, int)"/>: wait-free, no loops, and a buffer is never owned by two parties.</item>
/// <item>SPSC only: exactly one writer thread and one reader thread; no buffer reference escapes its lease (nothing
/// read from <see cref="Front"/> is kept after the next acquire, nothing from <see cref="Back"/> after the next publish).</item>
/// <item>Each buffer carries an owner tag (W/M/R) checked on every transition; a mismatch is counted in
/// <see cref="Violations"/> (the stress tests require 0).</item>
/// <item>Reader → writer feedback: <see cref="StoreAck"/> (seq of the last successfully applied publication) and
/// <see cref="StoreDispatched"/> (change seq of the last dispatched event). Single writer each (the reader).</item>
/// </list>
/// </summary>
internal sealed class ViewRing<T> where T : class
{
    private const int Fresh = 4;
    private const int IndexMask = 3;
    internal const int TagWriter = 1;
    internal const int TagMiddle = 2;
    internal const int TagReader = 3;

    private readonly T[] _buffers;
    private readonly int[] _tags = [TagWriter, TagMiddle, TagReader];
    private int _state = 1;
    private int _back;
    private int _front = 2;
    private int _violations;
    private long _ackSeq;
    private long _dispatchedCs;

    public ViewRing(T b0, T b1, T b2) => _buffers = [b0, b1, b2];

    // ---- writer side ----
    public T Back => _buffers[_back];
    public int BackIndex => _back;

    /// <summary>Hands the filled back buffer to the reader. Returns true when the previous publication was never acquired
    /// (overwritten in the middle slot, i.e. skipped).</summary>
    public bool Publish()
    {
        int back = _back;
        Expect(back, TagWriter);
        Volatile.Write(ref _tags[back], TagMiddle);
        int old = Interlocked.Exchange(ref _state, back | Fresh);
        int next = old & IndexMask;
        Expect(next, TagMiddle);
        Volatile.Write(ref _tags[next], TagWriter);
        _back = next;
        return (old & Fresh) != 0;
    }

    public long ReadAck() => Volatile.Read(ref _ackSeq);
    public long ReadDispatched() => Volatile.Read(ref _dispatchedCs);

    // ---- reader side ----
    public T Front => _buffers[_front];
    public int FrontIndex => _front;

    /// <summary>Takes the latest publication if one is fresh; otherwise keeps the current front and returns false.</summary>
    public bool TryAcquire()
    {
        if ((Volatile.Read(ref _state) & Fresh) == 0)
        {
            return false;
        }
        int front = _front;
        Expect(front, TagReader);
        Volatile.Write(ref _tags[front], TagMiddle);
        int old = Interlocked.Exchange(ref _state, front);
        int next = old & IndexMask;
        Expect(next, TagMiddle);
        Volatile.Write(ref _tags[next], TagReader);
        _front = next;
        return true;
    }

    public void StoreAck(long seq) => Volatile.Write(ref _ackSeq, seq);
    public void StoreDispatched(long cs) => Volatile.Write(ref _dispatchedCs, cs);

    // ---- diagnostics ----
    public int Violations => Volatile.Read(ref _violations);
    internal int TagOf(int index) => Volatile.Read(ref _tags[index]);

    private void Expect(int index, int tag)
    {
        if (Volatile.Read(ref _tags[index]) != tag)
        {
            Interlocked.Increment(ref _violations);
        }
    }
}
