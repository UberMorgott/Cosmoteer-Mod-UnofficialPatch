using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using static UnofficialPatch.Tests.TestPayloadModel;

namespace UnofficialPatch.Tests;

/// <summary>T13 (ring SPSC stress, payload checksums + owner tags), the two-thread protocol stress and T14 (randomized
/// model test against the independent oracle), decoupling spec §R10 F5 plus the review additions.</summary>
public class ViewStressTests(ITestOutputHelper output)
{
    private sealed class StressBuf
    {
        public long Seq;
        public readonly long[] Payload = new long[64];
        public long Checksum;
    }

    private static long Mix(long seq, int i) => unchecked((long)(((ulong)seq * 0x9E3779B97F4A7C15UL) ^ ((ulong)i * 0x632BE59BD9B4E019UL)));

    private sealed record RingStats(long Published, long Acquired, long Skipped, long MaxPublishTicks);

    /// <summary>One writer thread and one reader thread on one ring. The writer never waits (one Exchange per publish);
    /// the reader never sees a torn or partial buffer (every payload word and the checksum match its seq), seqs are
    /// strictly increasing, and no buffer ever has two owners (tag violations = 0).</summary>
    private static RingStats RunRing(int publishes, int seed, int writerSpin, int readerSpin)
    {
        var ring = new ViewRing<StressBuf>(new StressBuf(), new StressBuf(), new StressBuf());
        long acquired = 0;
        long skipped = 0;
        long maxTicks = 0;
        int done = 0;
        Exception? readerError = null;
        var reader = new Thread(() =>
        {
            try
            {
                var rng = new Random(seed + 1);
                long last = 0;
                while (true)
                {
                    // ReSharper disable once AccessToModifiedClosure -- cross-thread flag, set by the writer, polled here via Volatile.Read.
                    bool finished = Volatile.Read(ref done) != 0;
                    if (ring.TryAcquire())
                    {
                        StressBuf b = ring.Front;
                        if (ring.TagOf(ring.FrontIndex) != ViewRing<StressBuf>.TagReader)
                        {
                            throw new InvalidOperationException("front not reader-owned");
                        }
                        long seq = b.Seq;
                        if (seq <= last)
                        {
                            throw new InvalidOperationException($"seq {seq} after {last}");
                        }
                        long sum = 0;
                        for (int i = 0; i < b.Payload.Length; i++)
                        {
                            if (b.Payload[i] != Mix(seq, i))
                            {
                                throw new InvalidOperationException($"torn payload at seq {seq} word {i}");
                            }
                            sum += b.Payload[i];
                            if (readerSpin > 0 && i == 32)
                            {
                                Thread.SpinWait(rng.Next(readerSpin)); // hold the lease mid-read
                            }
                        }
                        if (sum != b.Checksum || b.Seq != seq)
                        {
                            throw new InvalidOperationException($"checksum mismatch at seq {seq}");
                        }
                        last = seq;
                        acquired++;
                    }
                    else if (finished)
                    {
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                readerError = e;
            }
        });
        reader.Start();
        var wrng = new Random(seed);
        for (long seq = 1; seq <= publishes; seq++)
        {
            StressBuf b = ring.Back;
            long sum = 0;
            for (int i = 0; i < b.Payload.Length; i++)
            {
                long v = Mix(seq, i);
                b.Payload[i] = v;
                sum += v;
            }
            b.Seq = seq;
            b.Checksum = sum;
            long t0 = Stopwatch.GetTimestamp();
            if (ring.Publish())
            {
                skipped++;
            }
            long dt = Stopwatch.GetTimestamp() - t0;
            if (dt > maxTicks)
            {
                maxTicks = dt;
            }
            if (writerSpin > 0)
            {
                Thread.SpinWait(wrng.Next(writerSpin));
            }
        }
        Volatile.Write(ref done, 1);
        reader.Join();
        Assert.Null(readerError);
        Assert.Equal(0, ring.Violations);
        Assert.True(acquired > 0);
        return new RingStats(publishes, acquired, skipped, maxTicks);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(1, 0, 50)]
    [InlineData(1, 50, 0)]
    [InlineData(2, 10, 10)]
    [InlineData(4, 20, 5)]
    [InlineData(8, 5, 20)]
    public void T13_RingSpscStress(int pairs, int writerSpin, int readerSpin)
    {
        int publishes = pairs == 1 ? 1_000_000 : 250_000;
        var results = new RingStats[pairs];
        Parallel.For(0, pairs, new ParallelOptions { MaxDegreeOfParallelism = pairs }, p =>
            results[p] = RunRing(publishes, 1000 + p, writerSpin, readerSpin));
        foreach (RingStats r in results)
        {
            output.WriteLine($"pairs={pairs} spin w{writerSpin}/r{readerSpin}: published={r.Published} acquired={r.Acquired} skipped={r.Skipped} maxPublishUs={r.MaxPublishTicks * 1e6 / Stopwatch.Frequency:F1}");
            Assert.Equal(r.Published, r.Acquired + r.Skipped);
        }
    }

    /// <summary>Random driver shared by T14 and the two-thread stress: creates/destroys (incl. lazy cascades), full and
    /// colour writes, writes to doomed children, events, reaps.</summary>
    private sealed class Driver(ViewHarness h, Random rng, int maxObjects)
    {
        private readonly List<ViewId>[] _pool = [[], [], [], []];

        public void Step()
        {
            switch (rng.Next(12))
            {
                case 0:
                    if (Count() < maxObjects)
                    {
                        _pool[Ship].Add(h.Create(Ship));
                    }
                    break;
                case 1:
                case 2:
                    CreateChild(Layer);
                    break;
                case 3:
                    CreateChild(Quad);
                    break;
                case 4:
                    CreateChild(Part);
                    break;
                case 5:
                    {
                        int k = rng.Next(4);
                        if (TryPick(k, out ViewId id))
                        {
                            h.SetFull(k, id);
                        }
                        break;
                    }
                case 6:
                case 7:
                    if (TryPick(Quad, out ViewId q))
                    {
                        h.SetColour(q, rng.Next(3) == 0);
                    }
                    break;
                case 8:
                    {
                        int k = rng.Next(4);
                        if (rng.Next(3) == 0 && TryPick(k, out ViewId id))
                        {
                            h.Destroy(k, id, lazy: k != Quad && rng.Next(2) == 0);
                        }
                        break;
                    }
                case 9:
                    // A write to a doomed child: legal sim-side, dropped by oracle and reader (stale op).
                    foreach (ViewId d in _pool[Quad])
                    {
                        if (h.A.IsResolvable(Quad, d) && !h.A.IsAlive(Quad, d))
                        {
                            h.A.SetFull(Quad, d, h.RandomBytes(Quad));
                            break;
                        }
                    }
                    break;
                case 10:
                    h.Emit(rng.Next(4));
                    break;
                default:
                    if (rng.Next(4) == 0)
                    {
                        h.A.Reap();
                    }
                    break;
            }
        }

        private int Count() => _pool[0].Count + _pool[1].Count + _pool[2].Count + _pool[3].Count;

        private void CreateChild(int kind)
        {
            if (Count() < maxObjects && TryPick(ParentKindOf(kind), out ViewId parent))
            {
                _pool[kind].Add(h.Create(kind, parent));
            }
        }

        private bool TryPick(int kind, out ViewId id)
        {
            List<ViewId> pool = _pool[kind];
            _pool[kind].RemoveAll(x => !h.A.IsResolvable(kind, x));
            for (int tries = 0; tries < 4 && pool.Count > 0; tries++)
            {
                id = pool[rng.Next(pool.Count)];
                if (h.A.IsAlive(kind, id))
                {
                    return true;
                }
            }
            id = default;
            return false;
        }
    }

    /// <summary>Per applied publication: dependency closure and "no colour op without an applied full op of the same
    /// (kind, slot, gen)" (the writer can only know of an ack for a publication the reader applied).</summary>
    private sealed class ProtocolChecker
    {
        private readonly HashSet<(int, uint, uint)> _known = [];
        private readonly HashSet<(int, uint, uint)> _fullApplied = [];
        public int Checked { get; private set; }
        public int ColourOps { get; private set; }

        public void OnApplied(ViewPublication pub)
        {
            ViewProtocolTests.AssertDependencyClosed(pub, _known);
            for (int i = 0; i < pub.ValueCount; i++)
            {
                ViewValueRec r = pub.Values[i];
                if (r.Op == ViewValueOp.Colour)
                {
                    ColourOps++;
                    Assert.True(_fullApplied.Contains((r.Kind, r.Id.Slot, r.Id.Gen)), $"colour op for {r.Kind}/{r.Id} without an applied full base");
                }
            }
            for (int i = 0; i < pub.ValueCount; i++)
            {
                ViewValueRec r = pub.Values[i];
                if (r.Op == ViewValueOp.Full)
                {
                    _fullApplied.Add((r.Kind, r.Id.Slot, r.Id.Gen));
                }
            }
            Checked++;
        }
    }

    private static void Configure(ViewHarness h, Random rng)
    {
        if (h.Writer.Mode == ViewWriterMode.Chunked)
        {
            h.Writer.ChunkItemCap = rng.Next(1, 24);
            h.Writer.ChunkLifeCap = rng.Next(3) == 0 ? int.MaxValue : rng.Next(1, 8);
            h.Writer.MaxPayloadBytes = rng.Next(3) == 0 ? rng.Next(16, 400) : 1 << 20;
        }
        else
        {
            h.Writer.MaxPayloadBytes = rng.Next(2) == 0 ? 1 << 20 : rng.Next(100, 2000);
        }
    }

    /// <summary>T14: seeded random op/skip/cap/stall/fault sequences, 10^4 cases. At every commit the drawable equals the
    /// oracle state at that publication; every applied publication is dependency-closed; no colour op without an applied
    /// full base; events exactly once in order.</summary>
    [Fact]
    public void T14_RandomModel()
    {
        int failures = 0, eventFailures = 0, terminal = 0, colourOps = 0, stale = 0, dropped = 0, overflow = 0;
        for (int seed = 0; seed < 10_000; seed++)
        {
            var rng = new Random(seed);
            var h = new ViewHarness(rng.Next(2) == 0 ? ViewWriterMode.FailClosed : ViewWriterMode.Chunked, seed);
            Configure(h, rng);
            h.Sink.Rng = rng;
            h.Reader.MaxEventRetries = int.MaxValue; // retryable faults are random here, not poisoned
            var driver = new Driver(h, rng, 40);
            var checker = new ProtocolChecker();
            int steps = rng.Next(10, 40);
            for (int step = 0; step < steps; step++)
            {
                for (int n = rng.Next(1, 5); n > 0; n--)
                {
                    driver.Step();
                }
                if (rng.Next(10) < 7)
                {
                    h.Publish();
                }
                if (rng.Next(10) < 6)
                {
                    h.Reader.InjectFaultAtOp = rng.Next(10) == 0 ? rng.Next(6) : -1;
                    h.Sink.ThrowProbability = rng.Next(10) == 0 ? 0.3 : 0;
                    h.Sink.AfterWorkProbability = rng.Next(20) == 0 ? 0.2 : 0;
                    ApplyChecked(h, checker);
                }
                if (rng.Next(20) == 0)
                {
                    Configure(h, rng);
                }
            }
            h.Reader.InjectFaultAtOp = -1;
            h.Sink.ThrowProbability = 0;
            h.Sink.AfterWorkProbability = 0;
            long last = 0;
            for (int guard = 0; ; guard++)
            {
                Assert.True(guard < 5000, $"seed {seed}: no final commit");
                if (h.Reader.CommittedSeq == last && last != 0 && h.Sink.Dispatched.Count == h.Oracle.Events.Count)
                {
                    break;
                }
                last = h.Publish().Seq;
                ApplyChecked(h, checker);
                if (h.Reader.CommittedSeq != last)
                {
                    last = 0;
                }
            }
            h.AssertDrawableIs(h.Reader.CommittedSeq);
            h.AssertEventsExactlyOnce();
            Assert.Equal(0, h.D.OrphanCreates);
            Assert.Equal(0, h.D.ImplicitKills);
            Assert.Equal(0, h.Writer.UnprovenGone);
            Assert.Equal(0, h.Reader.SeqViolations);
            Assert.Equal(0, h.Ring.Violations);
            failures += h.Reader.Failures;
            eventFailures += h.Reader.EventFailures;
            terminal += h.Reader.TerminalEventFailures;
            colourOps += checker.ColourOps;
            stale += h.D.StaleValueOps;
            dropped += h.Writer.DroppedGone;
            overflow += h.Writer.Overflow;
        }
        output.WriteLine($"T14: 10000 cases, failures={failures} eventFailures={eventFailures} terminalAfterWork={terminal} colourOps={colourOps} staleValueOps={stale} droppedGone={dropped} overflow={overflow}");
        Assert.True(failures > 0 && eventFailures > 0 && terminal > 0 && colourOps > 0 && stale > 0 && dropped > 0);
    }

    private static void ApplyChecked(ViewHarness h, ProtocolChecker checker)
    {
        long acked = h.Reader.AckedSeq;
        h.Apply();
        if (h.Reader.AckedSeq != acked)
        {
            checker.OnApplied(h.Reader.Current!);
        }
    }

    /// <summary>Full protocol on two threads (writer: random ops + publish; reader: apply + oracle check at every
    /// commit), with reader faults, event faults and varied timings; then a drain that must deliver every event once.</summary>
    [Theory]
    [InlineData(false, 0, 0, 1)]
    [InlineData(false, 200, 0, 2)]
    [InlineData(false, 0, 200, 3)]
    [InlineData(true, 0, 0, 4)]
    [InlineData(true, 100, 100, 5)]
    public void ProtocolTwoThreadStress(bool chunked, int writerSpin, int readerSpin, int seed)
    {
        const int steps = 20_000;
        var h = new ViewHarness(chunked ? ViewWriterMode.Chunked : ViewWriterMode.FailClosed, seed);
        var wrng = new Random(seed);
        Configure(h, wrng);
        if (chunked)
        {
            h.Writer.MaxPayloadBytes = 1 << 20;
        }
        var driver = new Driver(h, wrng, 60);
        var checker = new ProtocolChecker();
        int stop = 0;
        int faults = 1;
        Exception? readerError = null;
        int commits = 0;
        var reader = new Thread(() =>
        {
            try
            {
                var rrng = new Random(seed + 77);
                h.Sink.Rng = rrng;
                h.Reader.MaxEventRetries = int.MaxValue;
                // ReSharper disable once AccessToModifiedClosure -- cross-thread flag, set by the writer, polled here via Volatile.Read.
                while (Volatile.Read(ref stop) == 0)
                {
                    // ReSharper disable once AccessToModifiedClosure -- cross-thread flag, set by the writer, polled here via Volatile.Read.
                    bool inject = Volatile.Read(ref faults) != 0;
                    h.Reader.InjectFaultAtOp = inject && rrng.Next(20) == 0 ? rrng.Next(6) : -1;
                    h.Sink.ThrowProbability = inject ? 0.02 : 0;
                    h.Sink.AfterWorkProbability = inject ? 0.01 : 0;
                    long committed = h.Reader.CommittedSeq;
                    ApplyChecked(h, checker);
                    if (h.Reader.CommittedSeq != committed)
                    {
                        commits++;
                        h.Oracle.Prune(h.Reader.CommittedSeq);
                    }
                    if (readerSpin > 0)
                    {
                        Thread.SpinWait(rrng.Next(readerSpin));
                    }
                }
            }
            catch (Exception e)
            {
                readerError = e;
            }
        });
        reader.Start();
        try
        {
            for (int i = 0; i < steps && readerError == null; i++)
            {
                driver.Step();
                if (wrng.Next(3) != 0)
                {
                    h.Publish();
                }
                if (writerSpin > 0)
                {
                    Thread.SpinWait(wrng.Next(writerSpin));
                }
            }
            Volatile.Write(ref faults, 0);
            var sw = Stopwatch.StartNew();
            while (readerError == null && sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                ViewPublishResult r = h.Publish();
                Thread.Sleep(1);
                if (r.Complete && r.Events == 0 && h.Ring.ReadAck() == r.Seq)
                {
                    break;
                }
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            reader.Join();
        }
        Assert.Null(readerError);
        h.AssertDrawableIs(h.Reader.CommittedSeq);
        h.AssertEventsExactlyOnce();
        Assert.Equal(0, h.Ring.Violations);
        Assert.Equal(0, h.Reader.SeqViolations);
        Assert.Equal(0, h.Writer.UnprovenGone);
        output.WriteLine($"two-thread {(chunked ? "chunked" : "failclosed")} spin w{writerSpin}/r{readerSpin}: published={h.Writer.NextSeq - 1} commits={commits} applied={checker.Checked} skipped={h.Writer.Skipped} failures={h.Reader.Failures} eventFailures={h.Reader.EventFailures} terminalAfterWork={h.Reader.TerminalEventFailures} colourOps={checker.ColourOps} events={h.Sink.Dispatched.Count}");
    }
}
