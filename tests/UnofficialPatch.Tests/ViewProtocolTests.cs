using System;
using System.Collections.Generic;
using Xunit;
using static UnofficialPatch.Tests.TestPayloadModel;

namespace UnofficialPatch.Tests;

/// <summary>Render-view protocol tests T01-T12 (decoupling spec §R10 F5) plus the review additions.</summary>
public class ViewProtocolTests
{
    /// <summary>Creates ship + layer (+ quads), publishes and commits; returns the committed seq.</summary>
    private static long Baseline(ViewHarness h, out ViewId ship, out ViewId layer, int quads, List<ViewId>? outQuads = null)
    {
        ship = h.Create(Ship);
        layer = h.Create(Layer, ship);
        for (int i = 0; i < quads; i++)
        {
            ViewId q = h.Create(Quad, layer);
            outQuads?.Add(q);
        }
        long seq = h.Publish().Seq;
        Assert.Equal(ViewApplyStatus.Committed, h.Apply());
        Assert.Equal(seq, h.Reader.CommittedSeq);
        return seq;
    }

    /// <summary>Publishes and applies until the writer has processed the reader's last ack (nothing left dirty).</summary>
    private static long Settle(ViewHarness h)
    {
        long seq = h.Publish().Seq;
        Assert.Equal(ViewApplyStatus.Committed, h.Apply());
        return seq;
    }

    private static ViewValueRec Op(ViewHarness h, int kind, ViewId id) =>
        ViewHarness.FindValue(h.Reader.Current!, kind, id) ?? throw new Xunit.Sdk.XunitException($"no value op for {kind}/{id}");

    [Fact]
    public void T01_ColourNeedsAckedBase()
    {
        var h = new ViewHarness();
        ViewId s = h.Create(Ship);
        ViewId l = h.Create(Layer, s);
        ViewId q = h.Create(Quad, l);
        h.Publish(); // P1: the reader never acquires it
        h.SetColour(q);
        ViewPublishResult p2 = h.Publish();
        Assert.True(p2.SkippedPrevious);
        Assert.Equal(ViewApplyStatus.Committed, h.Apply());
        Assert.Equal(p2.Seq, h.Reader.CommittedSeq);
        Assert.Equal(ViewValueOp.Full, Op(h, Quad, q).Op);
        Assert.True(h.D.DataOf(Quad, q.Slot).SequenceEqual(h.A.Data(Quad, q.Slot)));
    }

    /// <summary>Review: the acked full base is per entry. Another quad's ack at a higher cs (and the lifecycle/value cut
    /// of an acked chunk) must not authorise a colour op for a quad whose own full op was never acked.</summary>
    [Fact]
    public void T01b_AckedBaseIsPerEntry()
    {
        var h = new ViewHarness(ViewWriterMode.Chunked);
        Baseline(h, out _, out ViewId l, 0);
        ViewId a = h.Create(Quad, l);
        Settle(h);
        h.Publish();
        h.Apply(); // writer has now processed the ack of a's full op
        Assert.False(h.Writer.NeedsFull(Quad, a));

        h.Writer.ChunkLifeCap = 1;
        ViewId x = h.Create(Quad, l); // life cs n
        ViewId b = h.Create(Quad, l); // life cs n+1, gated out of the next chunk
        h.SetFull(Quad, a);           // value cs n+2 > b's full cs
        ViewPublishResult chunk = h.Publish();
        Assert.False(chunk.Complete);
        Assert.Equal(ViewApplyStatus.Staged, h.Apply());
        Assert.NotNull(ViewHarness.FindValue(h.Reader.Current!, Quad, a));
        Assert.NotNull(ViewHarness.FindValue(h.Reader.Current!, Quad, x));
        Assert.Null(ViewHarness.FindValue(h.Reader.Current!, Quad, b));

        h.SetColour(b);
        h.Publish(); // processes the chunk ack: a acked at a cs above b's full cs
        Assert.False(h.Writer.NeedsFull(Quad, a));
        Assert.True(h.Writer.NeedsFull(Quad, b));
        h.Apply();
        Assert.Equal(ViewValueOp.Full, Op(h, Quad, b).Op);
        DrainToCommit(h);
    }

    /// <summary>Review: the acked base is per generation; a reused slot never inherits the old generation's ack.</summary>
    [Fact]
    public void T01c_AckedBaseIsPerGeneration()
    {
        var h = new ViewHarness();
        Baseline(h, out _, out ViewId l, 0);
        ViewId q1 = h.Create(Quad, l);
        Settle(h);
        h.SetColour(q1);
        Settle(h);
        Assert.Equal(ViewValueOp.Colour, Op(h, Quad, q1).Op); // sanity: gen 1 has an acked base

        h.Destroy(Quad, q1);
        ViewId q2 = h.Create(Quad, l);
        Assert.Equal(q1.Slot, q2.Slot);
        Assert.Equal(q1.Gen + 1, q2.Gen);
        h.Publish(); // skipped
        h.SetColour(q2);
        h.Publish();
        Assert.Equal(ViewApplyStatus.Committed, h.Apply());
        Assert.Equal(ViewValueOp.Full, Op(h, Quad, q2).Op);
        Assert.False(h.D.TryResolve(Quad, q1));
    }

    [Theory]
    [InlineData(false, 16)]
    [InlineData(true, 4)]
    public void T02_ColourAfterAckedFull(bool uniform, int length)
    {
        var h = new ViewHarness();
        Baseline(h, out _, out ViewId l, 0);
        ViewId q = h.Create(Quad, l);
        Settle(h);
        h.SetColour(q, uniform);
        Settle(h);
        ViewValueRec op = Op(h, Quad, q);
        Assert.Equal(ViewValueOp.Colour, op.Op);
        Assert.Equal(length, op.Length);
    }

    /// <summary>T03 with failure injection: a failed apply acks nothing, keeps the previous complete view, dispatches no
    /// event; the full op stays sticky and the retry dispatches each event exactly once.</summary>
    [Fact]
    public void T03_FullStickyUntilAck()
    {
        var h = new ViewHarness();
        long baseline = Baseline(h, out _, out ViewId l, 0);
        ViewId q = h.Create(Quad, l);
        h.Emit();
        h.Emit();
        h.Emit();
        h.Publish();
        h.Reader.InjectFaultAtOp = 1;
        Assert.Equal(ViewApplyStatus.Failed, h.Apply());
        Assert.Equal(baseline, h.Reader.AckedSeq);
        Assert.Equal(baseline, h.Ring.ReadAck());
        h.AssertDrawableIs(baseline);
        Assert.Empty(h.Sink.Dispatched);

        h.Reader.InjectFaultAtOp = -1;
        h.SetColour(q);
        Assert.True(h.Writer.NeedsFull(Quad, q));
        h.Publish();
        Assert.Equal(ViewApplyStatus.Committed, h.Apply());
        Assert.Equal(ViewValueOp.Full, Op(h, Quad, q).Op);
        h.AssertEventsExactlyOnce();
    }

    /// <summary>Checks a publication is dependency-closed against the creates the reader already holds, then adds its
    /// creates. Returns the number of checked ops.</summary>
    internal static void AssertDependencyClosed(ViewPublication pub, HashSet<(int, uint, uint)> known)
    {
        var local = new HashSet<(int, uint, uint)>(known);
        for (int i = 0; i < pub.LifeCount; i++)
        {
            ViewLifeRec r = pub.Life[i];
            if (r.Op == ViewLifeOp.Create)
            {
                if (r.ParentKind >= 0)
                {
                    Assert.True(local.Contains((r.ParentKind, r.Parent.Slot, r.Parent.Gen)), $"create {r.Kind}/{r.Id} without its parent");
                }
                local.Add((r.Kind, r.Id.Slot, r.Id.Gen));
            }
            else
            {
                Assert.True(local.Contains((r.Kind, r.Id.Slot, r.Id.Gen)), $"destroy {r.Kind}/{r.Id} without its create");
            }
        }
        for (int i = 0; i < pub.ValueCount; i++)
        {
            ViewValueRec r = pub.Values[i];
            Assert.True(local.Contains((r.Kind, r.Id.Slot, r.Id.Gen)), $"value {r.Kind}/{r.Id} without its create");
        }
        known.UnionWith(local);
    }

    private static int DrainToCommit(ViewHarness h, HashSet<(int, uint, uint)>? known = null, long? drawableSeq = null)
    {
        int chunks = 0;
        for (int guard = 0; guard < 10_000; guard++)
        {
            ViewApplyStatus st = h.Apply();
            if (st is ViewApplyStatus.Staged or ViewApplyStatus.Committed && known != null)
            {
                AssertDependencyClosed(h.Reader.Current!, known);
            }
            if (st == ViewApplyStatus.Committed)
            {
                return chunks;
            }
            if (st == ViewApplyStatus.Staged)
            {
                chunks++;
                if (drawableSeq != null)
                {
                    h.AssertDrawableIs(drawableSeq.Value);
                }
            }
            h.Publish();
        }
        throw new Xunit.Sdk.XunitException("no complete publication");
    }

    [Fact]
    public void T04_DepClosedCut()
    {
        var h = new ViewHarness(ViewWriterMode.Chunked);
        h.Writer.ChunkItemCap = 2;
        ViewId s = h.Create(Ship);    // cs1
        ViewId l = h.Create(Layer, s); // cs2
        h.Create(Quad, l);            // cs3
        h.SetFull(Ship, s);           // cs4
        var known = new HashSet<(int, uint, uint)>();
        ViewPublishResult first = h.Publish();
        Assert.False(first.Complete);
        int chunks = DrainToCommit(h, known);
        Assert.True(chunks >= 2, $"{chunks} chunks");
        Assert.Equal(3, known.Count);
    }

    [Fact]
    public void T05_NoPartialDraw()
    {
        var h = new ViewHarness(ViewWriterMode.Chunked, seed: 5);
        ViewId s = h.Create(Ship);
        var quads = new List<ViewId>();
        for (int i = 0; i < 4; i++)
        {
            ViewId l = h.Create(Layer, s);
            for (int j = 0; j < 250; j++)
            {
                quads.Add(h.Create(Quad, l));
            }
        }
        h.Publish();
        Assert.Equal(ViewApplyStatus.Committed, h.Apply());
        long baseline = Settle(h);

        h.Writer.ChunkItemCap = 64;
        var rng = new Random(5);
        for (int tick = 0; tick < 5; tick++) // reader stalled
        {
            for (int i = 0; i < 200; i++)
            {
                ViewId q = quads[(tick * 200) + i];
                if (rng.Next(2) == 0)
                {
                    h.SetFull(Quad, q);
                }
                else
                {
                    h.SetColour(q, rng.Next(2) == 0);
                }
            }
            Assert.False(h.Publish().Complete);
        }
        int chunks = DrainToCommit(h, drawableSeq: baseline);
        Assert.True(chunks >= 1000 / 64, $"{chunks} chunks");
    }

    [Fact]
    public void T06_FailClosedM1()
    {
        var h = new ViewHarness();
        h.Writer.MaxPayloadBytes = 200;
        ViewId s = h.Create(Ship);
        ViewId l = h.Create(Layer, s);
        for (int i = 0; i < 50; i++)
        {
            h.Create(Quad, l);
        }
        ViewPublishResult p = h.Publish();
        Assert.True(p.Complete);
        Assert.Equal(1, h.Writer.Overflow);
        Assert.Equal(ViewApplyStatus.Committed, h.Apply());
        Assert.True(h.Reader.Current!.Overflow);
        Assert.Equal(p.Seq, h.Reader.CommittedSeq);
    }

    [Fact]
    public void T07_SkipCoalesce()
    {
        var h = new ViewHarness(seed: 7);
        var quads = new List<ViewId>();
        Baseline(h, out ViewId s, out ViewId l, 20, quads);
        int skippedBefore = h.Writer.Skipped;
        var rng = new Random(7);
        long last = 0;
        for (int p = 0; p < 10; p++)
        {
            h.SetFull(Quad, quads[rng.Next(quads.Count)]);
            h.SetColour(quads[rng.Next(quads.Count)]);
            quads.Add(h.Create(Quad, l));
            h.SetFull(Ship, s);
            last = h.Publish().Seq;
        }
        Assert.Equal(9, h.Writer.Skipped - skippedBefore);
        Assert.Equal(ViewApplyStatus.Committed, h.Apply());
        Assert.Equal(last, h.Reader.CommittedSeq);
    }

    [Fact]
    public void T08_SlotReuseGen()
    {
        var h = new ViewHarness();
        Baseline(h, out _, out ViewId l, 0);
        ViewId g1 = h.Create(Quad, l);
        h.Destroy(Quad, g1);
        ViewId g2 = h.Create(Quad, l);
        Assert.Equal(g1.Slot, g2.Slot);
        Assert.NotEqual(g1.Gen, g2.Gen);
        h.Publish();
        Assert.Equal(ViewApplyStatus.Committed, h.Apply());
        Assert.False(h.D.TryResolve(Quad, g1));
        Assert.True(h.D.TryResolve(Quad, g2));
        Assert.False(h.A.Slots(Quad).TryResolve(g1));
    }

    [Fact]
    public void T09_GenRetire()
    {
        var map = new ViewSlotMap();
        ViewId a = map.Alloc();
        ViewId forced = map.ForceGeneration(a, uint.MaxValue - 1);
        Assert.True(map.Free(forced));
        ViewId last = map.Alloc();
        Assert.Equal(a.Slot, last.Slot);
        Assert.Equal(uint.MaxValue, last.Gen);
        Assert.True(map.Free(last));
        Assert.True(map.IsRetired(a.Slot));
        Assert.Equal(1, map.Retired);
        Assert.False(map.Free(last));
        for (int i = 0; i < 100; i++)
        {
            Assert.NotEqual(a.Slot, map.Alloc().Slot);
        }
        Assert.False(map.TryResolve(a));
        Assert.False(map.TryResolve(forced));
        Assert.False(map.TryResolve(last));
        Assert.False(map.TryResolve(ViewId.None));
    }

    [Fact]
    public void T10_Cascade()
    {
        var h = new ViewHarness();
        ViewId s = h.Create(Ship);
        ViewId l1 = h.Create(Layer, s);
        ViewId l2 = h.Create(Layer, s);
        ViewId q1 = h.Create(Quad, l1);
        ViewId q2 = h.Create(Quad, l2);
        ViewId p = h.Create(Part, s);
        ViewId other = h.Create(Ship);
        ViewId otherLayer = h.Create(Layer, other);
        Settle(h);

        h.Destroy(Ship, s, lazy: true); // children stay resolvable sim-side (native dispose comes later)
        h.SetFull(Quad, q2);            // a value op for a doomed child: the oracle drops it, the reader must too
        h.Publish();
        Assert.Equal(ViewApplyStatus.Committed, h.Apply());
        foreach ((int k, ViewId id) in new[] { (Ship, s), (Layer, l1), (Layer, l2), (Quad, q1), (Quad, q2), (Part, p) })
        {
            Assert.False(h.D.TryResolve(k, id), $"{k}/{id} survived");
        }
        Assert.True(h.D.TryResolve(Layer, otherLayer));
        Assert.Equal(1, h.D.StaleValueOps);

        h.A.Reap();
        ViewId s2 = h.Create(Ship);
        ViewId l3 = h.Create(Layer, s2);
        h.Create(Quad, l3);
        Settle(h);
        Assert.Equal(s.Slot, s2.Slot);
        Assert.False(h.D.TryResolve(Ship, s));
    }

    /// <summary>T11 with failure injection: a validation fault keeps D at the previous commit and the ack unchanged; the
    /// next publication re-covers everything after the ack; retries and event faults never duplicate a command.</summary>
    [Fact]
    public void T11_AckOnlyOnApply()
    {
        var h = new ViewHarness(seed: 11);
        var quads = new List<ViewId>();
        Baseline(h, out ViewId s, out ViewId l, 10, quads);
        Settle(h);
        long good = Settle(h);

        // 1. Fault mid-way through the value ops.
        quads.Add(h.Create(Quad, l));
        quads.Add(h.Create(Quad, l));
        quads.Add(h.Create(Quad, l));
        h.Destroy(Quad, quads[0]);
        h.Destroy(Quad, quads[1]);
        h.SetFull(Quad, quads[2]);
        h.SetFull(Quad, quads[3]);
        h.SetFull(Ship, s);
        h.SetColour(quads[4]);
        h.SetColour(quads[5], uniform: true);
        for (int i = 0; i < 5; i++)
        {
            h.Emit();
        }
        ViewPublishResult p = h.Publish();
        h.Reader.InjectFaultAtOp = p.LifeOps + 2;
        Assert.Equal(ViewApplyStatus.Failed, h.Apply());
        Assert.Equal(good, h.Ring.ReadAck());
        h.AssertDrawableIs(good);
        Assert.Empty(h.Sink.Dispatched);

        ViewPublishResult again = h.Publish();
        Assert.Equal(p.LifeOps, again.LifeOps);
        Assert.Equal(p.ValueOps, again.ValueOps);
        Assert.Equal(5, again.Events);
        h.Reader.InjectFaultAtOp = again.LifeOps; // first value op
        Assert.Equal(ViewApplyStatus.Failed, h.Apply());
        h.AssertDrawableIs(good);

        // 2. Retry of the held publication (no new one).
        h.Reader.InjectFaultAtOp = -1;
        Assert.Equal(ViewApplyStatus.Committed, h.Apply());
        Assert.Equal(again.Seq, h.Reader.CommittedSeq);
        h.AssertEventsExactlyOnce();

        // 3. Event fault after the commit: the drawable is the new coherent view; the retry finishes the rest once.
        h.SetFull(Quad, quads[6]);
        for (int i = 0; i < 10; i++)
        {
            h.Emit();
        }
        ViewPublishResult e = h.Publish();
        h.Sink.ThrowOnAttempts.Add(h.Sink.Attempts + 4);
        Assert.Equal(ViewApplyStatus.EventFailed, h.Apply());
        h.AssertDrawableIs(e.Seq);
        Assert.Equal(e.Seq, h.Ring.ReadAck());
        Assert.Equal(5 + 4, h.Sink.Dispatched.Count);
        h.AssertEventsExactlyOnce(all: false);
        Assert.Equal(ViewApplyStatus.Committed, h.Apply());
        h.AssertEventsExactlyOnce();

        // 4. Event fault, then a newer publication carries the rest (writer has not seen the cursor move yet).
        for (int i = 0; i < 10; i++)
        {
            h.Emit();
        }
        h.Publish();
        h.Sink.ThrowOnAttempts.Add(h.Sink.Attempts + 2);
        Assert.Equal(ViewApplyStatus.EventFailed, h.Apply());
        h.SetFull(Quad, quads[7]);
        h.Emit();
        h.Publish();
        Assert.Equal(ViewApplyStatus.Committed, h.Apply());
        h.AssertEventsExactlyOnce();
    }

    [Fact]
    public void T12_EventLogOrder()
    {
        var h = new ViewHarness(seed: 12);
        var quads = new List<ViewId>();
        Baseline(h, out _, out ViewId l, 4, quads);
        for (int p = 0; p < 3; p++)
        {
            for (int i = 0; i < (p == 2 ? 334 : 333); i++)
            {
                h.Emit(i % 5);
                if (i % 50 == 0)
                {
                    h.SetFull(Quad, quads[i % quads.Count]);
                    quads.Add(h.Create(Quad, l));
                }
            }
            h.Publish(); // reader skips the first two
        }
        Assert.Equal(ViewApplyStatus.Committed, h.Apply());
        Assert.Equal(1000, h.Sink.Dispatched.Count);
        h.AssertEventsExactlyOnce();

        // Failure injection: the 500th dispatch of the next 1000 throws once; retry via a newer publication.
        for (int i = 0; i < 1000; i++)
        {
            h.Emit(i % 3);
        }
        h.Publish();
        h.Sink.ThrowOnAttempts.Add(h.Sink.Attempts + 499);
        Assert.Equal(ViewApplyStatus.EventFailed, h.Apply());
        Assert.Equal(1499, h.Sink.Dispatched.Count);
        h.Publish();
        Assert.Equal(ViewApplyStatus.Committed, h.Apply());
        h.AssertEventsExactlyOnce();
        Assert.Equal(2000, h.Sink.Dispatched.Count);

        // Hard cap: overflow is a counted protocol error, never a silent drop.
        h.Publish(); // writer trims the dispatched events
        h.Apply();
        h.Writer.MaxEvents = 10;
        for (int i = 0; i < 10; i++)
        {
            Assert.True(h.Emit());
        }
        Assert.False(h.Emit());
        Assert.Equal(1, h.Writer.EventOverflow);
    }

    /// <summary>Review regression: a source that loses a live, dirty object without any logged Destroy (no cascade
    /// provenance) must not be "dropped as gone": the writer counts it and never publishes complete, so the last
    /// complete view stays drawn and the gate fails, instead of the stale object surviving a "complete" view forever.</summary>
    [Fact]
    public void UnprovenGoneFailsClosed()
    {
        var h = new ViewHarness();
        Baseline(h, out _, out ViewId l, 0);
        ViewId q = h.Create(Quad, l);
        long withQ = Settle(h);
        h.SetFull(Quad, q);
        h.A.FreeSilently(Quad, q);
        for (int i = 0; i < 4; i++)
        {
            ViewPublishResult p = h.Publish();
            Assert.False(p.Complete);
            Assert.Equal(0, p.Events);
            Assert.Equal(ViewApplyStatus.Staged, h.Apply());
        }
        Assert.Equal(1, h.Writer.UnprovenGone);
        Assert.Equal(0, h.Writer.DroppedGone);
        Assert.Equal(withQ, h.Reader.CommittedSeq);
        Assert.True(h.D.TryResolve(Quad, q));
    }

    /// <summary>A silent free followed by slot reuse is counted on the writer; the reader replaces the old generation
    /// cleanly (implicit kill, counted) instead of leaving it linked.</summary>
    [Fact]
    public void UnprovenSlotReuseIsCounted()
    {
        var h = new ViewHarness();
        Baseline(h, out _, out ViewId l, 0);
        ViewId q = h.Create(Quad, l);
        Settle(h);
        Settle(h);
        h.A.FreeSilently(Quad, q);
        ViewId q2 = h.A.Create(Quad, l, new byte[48]);
        Assert.Equal(q.Slot, q2.Slot);
        Assert.Equal(1, h.Writer.UnprovenGone);
        h.A.Publish();
        Assert.Equal(ViewApplyStatus.Committed, h.Reader.Update(h.Sink));
        Assert.Equal(1, h.D.ImplicitKills);
        Assert.False(h.D.TryResolve(Quad, q));
        Assert.True(h.D.TryResolve(Quad, q2));
    }

    /// <summary>Review: a sink that throws AFTER its side effect (uncertain failure) is never retried: the effect happens
    /// exactly once, the failure is terminal (counted, logged) and dispatch resumes after it.</summary>
    [Fact]
    public void SinkThrowsAfterSideEffectIsNotRepeated()
    {
        var h = new ViewHarness();
        Baseline(h, out _, out _, 0);
        for (int i = 0; i < 5; i++)
        {
            h.Emit();
        }
        h.Publish();
        h.Sink.ThrowAfterWorkOnAttempts.Add(h.Sink.Attempts + 2);
        Assert.Equal(ViewApplyStatus.EventTerminal, h.Apply());
        Assert.Equal(1, h.Reader.TerminalEventFailures);
        Assert.Equal(3, h.Sink.Dispatched.Count);
        Assert.Equal(h.Sink.Dispatched[2].Cs, h.Reader.LastTerminalCs);
        Assert.Equal(ViewApplyStatus.Committed, h.Apply()); // same publication: resumes after the terminal one
        h.Emit();
        h.Publish();
        Assert.Equal(ViewApplyStatus.Committed, h.Apply()); // newer publication: nothing repeated
        h.AssertEventsExactlyOnce();
        Assert.Equal(6, h.Sink.Attempts);
    }

    /// <summary>Hole 3: a command that keeps failing retryably is poisoned after MaxEventRetries attempts (terminal,
    /// counted), never retried forever, and the commands after it still run.</summary>
    [Fact]
    public void PoisonedCommandBecomesTerminal()
    {
        var h = new ViewHarness();
        Baseline(h, out _, out _, 0);
        h.Emit(1);
        h.Emit(9);
        h.Emit(1);
        h.Sink.PoisonKind = 9;
        h.Publish();
        Assert.Equal(ViewApplyStatus.EventFailed, h.Apply());
        Assert.Equal(ViewApplyStatus.EventFailed, h.Apply());
        Assert.Equal(ViewApplyStatus.EventTerminal, h.Apply());
        Assert.Equal(ViewApplyStatus.Committed, h.Apply());
        Assert.Equal(1, h.Reader.TerminalEventFailures);
        Assert.Equal(3, h.Reader.EventFailures);
        Assert.Equal(2, h.Sink.Dispatched.Count);
        Assert.All(h.Sink.Dispatched, d => Assert.Equal(1, d.Kind));
        int attempts = h.Sink.Attempts;
        h.Publish();
        Assert.Equal(ViewApplyStatus.Committed, h.Apply());
        Assert.Equal(attempts, h.Sink.Attempts);
    }

    /// <summary>Steady-state publish + acquire + apply (value writes, colour ops, slot reuse, events) allocates nothing.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SteadyStatePublishApplyAllocatesNothing(bool chunked)
    {
        ViewWriterMode mode = chunked ? ViewWriterMode.Chunked : ViewWriterMode.FailClosed;
        var model = new TestPayloadModel();
        var ring = new ViewRing<ViewPublication>(new ViewPublication(), new ViewPublication(), new ViewPublication());
        var writer = new ViewWriter(ring, model, mode);
        if (mode == ViewWriterMode.Chunked)
        {
            writer.ChunkItemCap = 64;
        }
        var reader = new ViewReader(ring, model, new ViewTable(model));
        var a = new TestAuthority(writer);
        var sink = new CountingSink();
        byte[] full = new byte[48];
        byte[] colours = new byte[16];
        byte[] payload = new byte[8];
        ViewId ship = a.Create(Ship, default, new byte[16]);
        ViewId layer = a.Create(Layer, ship, new byte[8]);
        var quads = new ViewId[256];
        for (int i = 0; i < quads.Length; i++)
        {
            quads[i] = a.Create(Quad, layer, full);
        }
        uint x = 12345;

        void Step()
        {
            for (int j = 0; j < 16; j++)
            {
                x = x * 1664525 + 1013904223;
                full[0] = (byte)x;
                a.SetFull(Quad, quads[x % quads.Length], full);
                x = x * 1664525 + 1013904223;
                colours[3] = (byte)x;
                a.SetColour(quads[x % quads.Length], colours);
            }
            x = x * 1664525 + 1013904223;
            int k = (int)(x % quads.Length);
            a.Destroy(Quad, quads[k]);
            quads[k] = a.Create(Quad, layer, full);
            a.SetFull(Ship, ship, new ReadOnlySpan<byte>(full, 0, 16));
            payload[0] = (byte)x;
            a.Emit(1, payload);
            a.Emit(2, payload);
            a.Publish();
            reader.Update(sink);
        }

        for (int i = 0; i < 3000; i++)
        {
            Step();
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20_000; i++)
        {
            Step();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(reader.CommittedSeq > 0);
        Assert.Equal(0, ring.Violations);
        Assert.Equal(0, writer.Overflow);
    }
}
