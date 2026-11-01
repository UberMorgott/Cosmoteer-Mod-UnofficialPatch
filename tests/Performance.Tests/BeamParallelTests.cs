using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Cosmoteer.Crew;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Modes.Creative;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Defenses;
using Cosmoteer.Ships.Parts.Effects;
using Cosmoteer.Ships.Parts.Logic;
using Cosmoteer.Ships.Parts.Weapons;
using Cosmoteer.Simulation;
using Halfling.Geometry;
using Halfling.Physics2D.Collision;
using Halfling.Physics2D.Common;
using Halfling.Physics2D.Dynamics;
using Halfling.Random;
using Halfling.Scene2D.Physics;
using Halfling.Timing;
using HarmonyLib;
using Xunit;
using HitInfo = Cosmoteer.Ships.Parts.Weapons.BeamEmitter.HitInfo;
using V = Halfling.Geometry.Vector2;

namespace Performance.Tests;

[Collection(nameof(BeamParallelTests))]
public sealed class BeamParallelTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1", true)]
    public void ExplicitOptInOnly(string? value, bool expected) => Assert.Equal(expected, BeamParallel.IsRequested(value));

    [Fact]
    public void NativeDependenciesArePinned() => BeamParallel.ValidateNative();

    [Fact]
    public void PatchTargetsAreNotSharedGeneric()
    {
        foreach (MethodBase target in new MethodBase[] { BeamParallel.BucketTarget, BeamParallel.EmitTarget, BeamParallel.HitInfoTarget, BeamParallel.StopTarget,
            BeamParallel.AttenuateTarget, BeamParallel.DieTarget, BeamParallel.EffectTarget })
        {
            Assert.NotNull(target);
            Assert.False(SharedGenericRetarget.IsShared(target), target.Name);
        }
    }

    [Fact]
    public void CompatibilityCacheSeesEveryHarmonyChange()
    {
        var harmony = new Harmony("Performance.Tests.BeamParallel.State");
        try
        {
            BeamParallel.RememberHarmonyState();
            Assert.True(BeamParallel.HarmonyStateUnchanged());
            MethodInfo target = AccessTools.Method(typeof(BeamParallelTests), nameof(PatchTarget));
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(BeamParallelTests), nameof(PatchPrefix)));
            Assert.False(BeamParallel.HarmonyStateUnchanged());
            BeamParallel.RememberHarmonyState();
            Assert.True(BeamParallel.HarmonyStateUnchanged());
            harmony.Unpatch(target, HarmonyPatchType.Prefix, harmony.Id); // same key, replaced patch-info array
            Assert.False(BeamParallel.HarmonyStateUnchanged());
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static int PatchTarget() => 1;
    private static void PatchPrefix() { }

    [Fact]
    public void ValueEventHookIsInsertedBeforeBothNativeEvents()
    {
        var original = PatchProcessor.GetOriginalInstructions(BeamParallel.EmitTarget);
        var code = BeamParallel.EmitHook.Transpiler(PatchProcessor.GetOriginalInstructions(BeamParallel.EmitTarget)).ToList();
        Assert.Equal(original.Count + 6, code.Count);
        MethodInfo hook = AccessTools.Method(typeof(BeamParallel.EmitHook), nameof(BeamParallel.EmitHook.BeforeValueEvent));
        Assert.Equal(2, code.Count(c => c.Calls(hook)));
        Assert.Equal(original.SelectMany(c => c.labels).Count(), code.SelectMany(c => c.labels).Count());
    }

    // --- Differential: worker query vs native GetHitInfo -----------------------------------------------------------

    public static IEnumerable<object[]> Scenarios()
    {
        foreach (int workers in new[] { 1, 2, 8 })
            foreach (string scenario in new[] { "operational", "structural-penetration", "structural-blocking", "secondary", "miss", "keep-length", "init-id", "anti-phasing", "fixture-proxies", "offset", "shield", "crew", "crew-homeless", "crew-friendly", "crew-behind", "crew-secondary" })
                yield return [scenario, workers];
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void WorkerQueryMatchesNativeBitForBit(string scenario, int workers)
    {
        using var f = new Setup(scenario);
        // Cold: fresh worker threads, no thread-local traversal stacks or contexts.
        HitInfo native = f.Native();
        try
        {
            using var session = f.Speculate(workers);
            var record = Assert.Single(session.Session.Records);
            Assert.Equal(BeamParallel.Outcome.Ready, record.Outcome);
            HitInfo taken = f.Take(out bool accepted);
            Assert.True(accepted, f.Reasons());
            try
            {
                Assert.Null(BeamParallel.Difference(native, taken));
                Assert.True(f.RandAfterNative == f.RandState(), "replay must consume exactly the native RNG draws");
                // Each scenario reaches the native branch it names.
                switch (scenario)
                {
                    case "operational" or "fixture-proxies" or "anti-phasing" or "init-id":
                        Assert.NotNull(native.Operational.Hit.Part); Assert.NotNull(native.Structural.Part); Assert.True(native.AllStructuralHits!.Count >= 2); break;
                    case "structural-penetration": Assert.True(native.AllStructuralHits!.Count >= 2); break;
                    case "structural-blocking": Assert.Null(native.Operational.Hit.Part); Assert.NotNull(native.Structural.Part); Assert.Null(native.AllStructuralHits); break;
                    case "secondary": Assert.NotNull(native.Operational.Hit.Part); Assert.Null(native.AllStructuralHits); break;
                    case "miss": Assert.False(native.Operational.Hit.HasObject()); Assert.Equal(400f, native.Length); break;
                    case "keep-length": Assert.False(native.Operational.Hit.HasObject()); Assert.Equal(50f, native.Length); break;
                    case "shield": Assert.Same(f.Shield, native.Operational.Hit.Shield); break;
                    case "offset": Assert.NotNull(native.Structural.Part); break;
                    case "crew" or "crew-homeless" or "crew-secondary": Assert.Same(f.Crew, native.Operational.Hit.Crew); Assert.Null(native.Operational.Hit.Part); break;
                    case "crew-friendly": Assert.Null(native.Operational.Hit.Crew); Assert.NotNull(native.Operational.Hit.Part); break; // rejected crew: the ray passes on
                    case "crew-behind": Assert.Same(f.Crew, native.Operational.Hit.Crew); Assert.NotNull(native.Structural.Part); Assert.NotEmpty(native.AllStructuralHits!); break;
                }
            }
            finally { taken.Dispose(); }
        }
        finally { native.Dispose(); }
    }

    [Theory]
    [InlineData("structural-penetration")]
    [InlineData("crew-behind")]
    [InlineData("crew-secondary")]
    public void ResultDoesNotDependOnWorkerCount(string scenario)
    {
        string?[] results = new[] { 1, 2, 8 }.Select(workers =>
        {
            using var f = new Setup(scenario, beams: 12);
            using var session = f.Speculate(workers);
            Assert.All(session.Session.Records, r => Assert.Equal(BeamParallel.Outcome.Ready, r.Outcome));
            if (scenario.StartsWith("crew", StringComparison.Ordinal)) Assert.Same(f.Crew, session.Session.Records[0].Info.Operational.Hit.Crew);
            return string.Join("|", session.Session.Records.Select(r => $"{r.Outcome} {BitConverter.SingleToInt32Bits(r.Info.Length)} {BitConverter.SingleToInt32Bits(r.Info.EndPoint.X)} {r.Info.AllStructuralHits?.Count} {r.Info.Operational.Hit.Crew != null}"));
        }).ToArray();
        Assert.Equal(results[0], results[1]);
        Assert.Equal(results[0], results[2]);
    }

    [Fact]
    public void ChangedInputFallsBackAndInvalidatesTheRest()
    {
        using var f = new Setup("operational", beams: 3);
        using var session = f.Speculate(2);
        f.Beams[0]._length += 1f; // query input changed after the snapshot (isInit=false path reads _length)
        f.Beams[0]._hitStartTime = Time.Zero;
        _ = f.Take(out bool accepted, beam: 0);
        Assert.False(accepted);
        Assert.False(session.Session.Valid);
        _ = f.Take(out accepted, beam: 1);
        Assert.False(accepted);
        Assert.Contains(BeamParallel.Stats.Reasons.Keys, k => k.StartsWith("replay native: after barrier", StringComparison.Ordinal));
    }

    [Fact]
    public void BarrierInvalidatesRemainingRecords()
    {
        using var f = new Setup("operational", beams: 2);
        using var session = f.Speculate(1);
        HitInfo first = f.Take(out bool accepted, beam: 0);
        Assert.True(accepted, f.Reasons());
        first.Dispose();
        BeamParallel.DieHook.Prefix();
        _ = f.Take(out accepted, beam: 1);
        Assert.False(accepted);
    }

    [Fact]
    public void RemovedEmitterRecordIsDisposedUnused()
    {
        using var f = new Setup("structural-penetration", beams: 2);
        var session = f.Speculate(2);
        var hits = session.Session.Records.Select(r => r.Info.AllStructuralHits).ToList();
        Assert.All(hits, Assert.NotNull);
        session.Dispose(); // the bucket ends without the emitters running (removed or suppressed)
        Assert.All(hits, h => Assert.Empty(h!));
    }

    /// <summary>
    /// Every beam of a round crossing exact crew: all handled on workers and replayed in native order, each result equal to
    /// the native query at its replay point (the crew hit first, ship hits after).
    /// </summary>
    [Fact]
    public void CrewRoundReplaysEveryRecordInOrder()
    {
        using var f = new Setup("crew-behind", beams: 3);
        using var session = f.Speculate(2);
        Assert.All(session.Session.Records, r => Assert.Equal(BeamParallel.Outcome.Ready, r.Outcome));
        for (int i = 0; i < 3; i++)
        {
            HitInfo native = f.Native(i);
            HitInfo taken = f.Take(out bool accepted, i);
            try
            {
                Assert.True(accepted, f.Reasons());
                Assert.Null(BeamParallel.Difference(native, taken));
                Assert.Equal(i == 0, taken.Operational.Hit.Crew != null);
            }
            finally { taken.Dispose(); native.Dispose(); }
        }
        Assert.True(session.Session.Valid);
    }

    /// <summary>A derived crew type is unaudited: that beam runs natively and every later record is invalidated.</summary>
    [Fact]
    public void DerivedCrewTypeFallsBackAndInvalidatesTheRest()
    {
        using var f = new Setup("crew-type", beams: 2);
        using var session = f.Speculate(2);
        BeamParallel.Record first = session.Session.ByEmitter[f.Beams[0]], second = session.Session.ByEmitter[f.Beams[1]];
        Assert.Equal(BeamParallel.Outcome.Native, first.Outcome); Assert.Equal("crew type", first.Reason);
        Assert.Null(first.Info.AllStructuralHits);
        Assert.Equal(BeamParallel.Outcome.Ready, second.Outcome);
        long lost = BeamParallel.Stats.LostToFallback;
        _ = f.Take(out bool accepted);
        Assert.False(accepted);
        Assert.False(session.Session.Valid);
        _ = f.Take(out accepted, 1);
        Assert.False(accepted);
        Assert.Equal(lost + 1, BeamParallel.Stats.LostToFallback);
        Assert.Contains(BeamParallel.Stats.Reasons.Keys, k => k == "invalidate: native fallback");
        Assert.Contains(BeamParallel.Stats.Reasons.Keys, k => k == "replay native: crew type");
    }

    /// <summary>An unaudited crew filter type stops at the crew, before any native filter code runs on a worker.</summary>
    [Fact]
    public void DerivedCrewFilterRunsNatively()
    {
        using var f = new Setup("crew");
        var filter = Shell<ForeignFilter>(); filter.Crew = true; filter.Enemies = true; // AffectsCrew
        Set(f.Beams[0].Rules, "CrewCollisions", filter);
        using var session = f.Speculate(1);
        var record = Assert.Single(session.Session.Records);
        Assert.Equal(BeamParallel.Outcome.Native, record.Outcome);
        Assert.Equal("crew filter", record.Reason);
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "ClassCannotBeInstantiated", Justification = "Only uninitialized shells are created.")]
    private sealed class ForeignFilter : EffectFilter
    {
        private ForeignFilter() : base(Defaults.All) { }
    }

    [Fact]
    public void ChainShapeFixtureRunsNatively()
    {
        using var f = new Setup("chain");
        using var session = f.Speculate(1);
        var record = Assert.Single(session.Session.Records);
        Assert.Equal(BeamParallel.Outcome.Native, record.Outcome);
        Assert.Equal("shape", record.Reason);
        Assert.Null(record.Info.AllStructuralHits);
    }

    [Fact]
    public void UnknownPlayerRunsNatively()
    {
        using var f = new Setup("unknown-player");
        using var session = f.Speculate(1);
        var record = Assert.Single(session.Session.Records);
        Assert.Equal(BeamParallel.Outcome.Native, record.Outcome);
        Assert.Equal("player", record.Reason);
        Assert.False(f.Game._playerStates.ContainsKey(7)); // the worker never created the missing player
    }

    [Fact]
    public void ColdShieldsAreNeverWarmedOnWorkers()
    {
        using var f = new Setup("cold-shield", beams: 12);
        using var session = f.Speculate(8);
        Assert.Equal(12, session.Session.Records.Count);
        // Beams crossing the cold shield go native; the others never reach it.
        Assert.True(session.Session.Records.Count(r => r.Outcome == BeamParallel.Outcome.Native && r.Reason == "cold shield") >= 4);
        Assert.All(session.Session.Records, r => Assert.Null(r.Info.Operational.Hit.Shield));
        Assert.False(f.Shield!._cachedArcAngle.HasValue);
        Assert.False(f.Shield._cachedArcRadius.HasValue);
        Assert.False(f.Shield._hasRulesLoc);
        Assert.False(f.Shield._hasRulesRot);
    }

    [Fact]
    public void WorkerThreadStateHoldsNoSceneObjects()
    {
        using var f = new Setup("shield", beams: 4);
        var held = new System.Collections.Concurrent.ConcurrentBag<bool>();
        BeamParallel.OverrideForTests((count, run) =>
        {
            var thread = new Thread(() => { for (int i = 0; i < count; i++) run(i); held.Add(BeamParallel.Ctx.Current.Holds); });
            thread.Start(); thread.Join();
        }, allowTestOwner: true);
        try
        {
            BeamParallel.Session s = BeamParallel.BeginWith(f.Sim, [f.Manager])!;
            Assert.All(s.Records, r => Assert.Equal(BeamParallel.Outcome.Ready, r.Outcome));
            BeamParallel.End(s);
        }
        finally { BeamParallel.OverrideForTests(null, allowTestOwner: true); }
        Assert.Equal([false], held.ToArray());
    }

    [Fact]
    public void UnknownTargetGetterOrModifierIsNeverInvoked()
    {
        RecordingWeapon.Calls = 0; RecordingModifier.Calls = 0;
        using (var f = new Setup("operational", beams: 2))
        {
            f.Beams[0]._getTargetFrom = Shell<RecordingWeapon>();
            Set(f.Beams[1].Rules.Range, "Modifiers", new ValueModifier[] { Shell<RecordingModifier>() });
            (ulong, ulong) rand = f.RandState();
            float length = f.Beams[0]._length, scale = f.Beams[0]._effectsScale;
            using var session = f.Speculate(2);
            Assert.Empty(session.Session.Records); // the first unaudited beam stops the whole round
            Assert.Contains(BeamParallel.Stats.Reasons.Keys, k => k == "predict stop: target graph");
            Assert.Equal(rand, f.RandState());
            Assert.Equal(length, f.Beams[0]._length); Assert.Equal(scale, f.Beams[0]._effectsScale);
        }
        using (var f = new Setup("operational", beams: 2))
        {
            Set(f.Beams[0].Rules.Spread.Min, "Modifiers", new ValueModifier[] { Shell<RecordingModifier>() });
            (ulong, ulong) rand = f.RandState();
            using var session = f.Speculate(1);
            Assert.Empty(session.Session.Records); // unknown spread: nothing is predicted
            Assert.Contains(BeamParallel.Stats.Reasons.Keys, k => k == "predict stop: spread modifier");
            Assert.Equal(rand, f.RandState());
        }
        Assert.Equal(0, RecordingWeapon.Calls);
        Assert.Equal(0, RecordingModifier.Calls);
    }

    /// <summary>
    /// Beam 0 stops on an unknown shape: its native raycast is unaudited and may change other state (here: another shield),
    /// so beam 1's precomputed shield hit must be rejected.
    /// </summary>
    [Fact]
    public void UnauditedWorkerFallbackInvalidatesLaterRecords()
    {
        using var f = new Setup("chain-shield", beams: 2);
        using var session = f.Speculate(2);
        BeamParallel.Record first = session.Session.ByEmitter[f.Beams[0]], second = session.Session.ByEmitter[f.Beams[1]];
        Assert.Equal(BeamParallel.Outcome.Native, first.Outcome); Assert.Equal("shape", first.Reason);
        Assert.Equal(BeamParallel.Outcome.Ready, second.Outcome); Assert.Same(f.Shield, second.Info.Operational.Hit.Shield);
        _ = f.Take(out bool accepted);
        Assert.False(accepted);
        Set(f.Shield!, "_cachedArcRadius", (float?)1f); // what the unaudited native raycast could have done
        _ = f.Take(out accepted, 1);
        Assert.False(accepted);
        Assert.False(session.Session.Valid);
        Assert.Contains(BeamParallel.Stats.Reasons.Keys, k => k == "invalidate: native fallback");
    }

    /// <summary>
    /// A cold-shield stop: its native replay may warm caches through modifiers or chained virtuals, or reach objects no worker
    /// inspected. The later record must be rejected even when something it read changes afterwards.
    /// </summary>
    [Fact]
    public void ColdShieldFallbackInvalidatesLaterRecords()
    {
        using var f = new Setup("cold-shield", beams: 6);
        using var session = f.Speculate(2);
        BeamParallel.Record? stop = session.Session.Records.FirstOrDefault(r => r.Reason == "cold shield");
        BeamParallel.Record? later = session.Session.Records.LastOrDefault(r => r.Outcome == BeamParallel.Outcome.Ready);
        Assert.NotNull(stop); Assert.NotNull(later); Assert.True(stop.Order < later.Order);
        long lost = BeamParallel.Stats.LostToFallback;
        for (int i = 0; i <= stop.Order; i++) f.Take(out _, i).Dispose();
        Set(f.Shield!, "_cachedArcRadius", (float?)1f); // an unaudited native side effect
        _ = f.Take(out bool accepted, later.Order);
        Assert.False(accepted);
        Assert.False(session.Session.Valid);
        Assert.True(BeamParallel.Stats.LostToFallback > lost);
    }
    [Fact]
    public void UnknownRangeModifierIsNeverInvoked()
    {
        RecordingModifier.Calls = 0;
        using var f = new Setup("operational", beams: 2);
        Set(f.Beams[1].Rules.Range, "Modifiers", new ValueModifier[] { Shell<RecordingModifier>() });
        using var session = f.Speculate(2);
        Assert.Single(session.Session.Records); // beam 0 only; prediction stopped at beam 1
        Assert.Contains(BeamParallel.Stats.Reasons.Keys, k => k == "predict stop: range modifier");
        Assert.Equal(0, RecordingModifier.Calls);
    }

    /// <summary>An unaudited first beam whose native getter changes a shield input: no later record (same or other ship) survives.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnauditedBeamStopsTheRoundBeforeLaterRecords(bool otherShip)
    {
        using var f = new Setup("shield", beams: otherShip ? 1 : 2, secondShip: otherShip);
        BeamEmitter first = f.Beams[0], second = otherShip ? f.SecondBeams[0] : f.Beams[1];
        first._getTargetFrom = Shell<MutatingWeapon>();
        MutatingWeapon.Shield = f.Shield;
        try
        {
            using var session = f.Speculate(2);
            Assert.False(session.Session.ByEmitter.ContainsKey(second));
            Assert.Contains(BeamParallel.Stats.Reasons.Keys, k => k == "predict stop: target graph");
            _ = first.GetShipTarget(checkProhibited: true); // the first beam's native run: its getter shrinks the shield
            Assert.Equal(1f, f.Shield!._cachedArcRadius);
            _ = f.Take(out bool accepted, second);
            Assert.False(accepted);
        }
        finally { MutatingWeapon.Shield = null; }
    }

    [Fact]
    public void OffsetDrawMismatchRestoresNativeRng()
    {
        using var f = new Setup("offset");
        using var session = f.Speculate(1);
        f.Beams[0].Rules.RandomOffsetRadius!.Value.Max.BaseValue = 9f; // offset circle changed after the snapshot
        _ = f.Take(out bool accepted);
        Assert.False(accepted);
        Assert.Equal(f.StateBeforeTake, f.RandState()); // the replay draw was rolled back: native draws it again
        Assert.Contains(BeamParallel.Stats.Reasons.Keys, k => k == "replay native: input offset draw");
    }

    [Fact]
    public void PatchedReplayPipelineMatchesNative()
    {
        var harmony = new Harmony("Performance.Tests.BeamParallel.Pipeline");
        try
        {
            harmony.Patch(BeamParallel.HitInfoTarget, prefix: new HarmonyMethod(typeof(BeamParallel.TakeHook), nameof(BeamParallel.TakeHook.Prefix)),
                postfix: new HarmonyMethod(typeof(BeamParallel.TakeHook), nameof(BeamParallel.TakeHook.Postfix)));
            foreach (string scenario in new[] { "structural-penetration", "offset", "shield" })
            {
                using var f = new Setup(scenario);
                HitInfo native = f.Native(); // no session: the patched method runs natively
                try
                {
                    using var session = f.Speculate(2);
                    long accepted = BeamParallel.Stats.Accepted;
                    BeamParallel.EmitHook.BeforeValueEvent(f.Beams[0], 0);
                    HitInfo replayed = f.PatchedNative();
                    BeamParallel.EmitHook.BeforeValueEvent(f.Beams[0], 1);
                    try
                    {
                        Assert.Equal(accepted + 1, BeamParallel.Stats.Accepted);
                        Assert.Null(BeamParallel.Difference(native, replayed));
                        Assert.Equal(f.RandAfterNative, f.RandState());
                    }
                    finally { replayed.Dispose(); }
                }
                finally { native.Dispose(); }
            }
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "ClassCannotBeInstantiated", Justification = "Only uninitialized shells are created.")]
    private sealed class RecordingWeapon : FixedWeapon
    {
        internal static int Calls;
        private RecordingWeapon() : base(null!) { }
        public override Ship? GetShipTarget(bool checkRange, bool checkProhibited) { Calls++; return null; }
        public override ITarget? GetFireTarget(bool checkRange, bool checkArc, bool checkProhibited, bool includeExplicitTarget) { Calls++; return null; }
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "ClassCannotBeInstantiated", Justification = "Only uninitialized shells are created.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "ClassCannotBeInstantiated", Justification = "Only uninitialized shells are created.")]
    private sealed class MutatingWeapon : FixedWeapon
    {
        internal static ArcShield? Shield;
        private MutatingWeapon() : base(null!) { }
        public override Ship? GetShipTarget(bool checkRange, bool checkProhibited) { if (Shield != null) Shield._cachedArcRadius = 1f; return null; }
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "ClassCannotBeInstantiated", Justification = "Only uninitialized shells are created.")]
    private sealed class RecordingModifier : BuffModifier
    {
        internal static int Calls;
        private RecordingModifier() : base(default(ID<Cosmoteer.Ships.Buffs.BuffType>)) { }
        public override float GetValue<TStatus>(float value, in ValueModificationData<TStatus> data) { Calls++; return value; }
    }

    // --- Listener admission ------------------------------------------------------------------------------------------

    [Fact]
    public void ListenerAdmission()
    {
        using var f = new Setup("operational");
        BeamEmitter beam = f.Beams[0];
        Assert.True(BeamParallel.Admitted(beam, out _));

        // Audited prism wiring: PartMultiValue with no downstream subscriber, sources exact emitters.
        PartMultiValue value = f.MultiValue(beam, "cosmoteer.ion_beam_prism", "EmitterValue");
        Subscribe(beam, value, "OnValueChanged");
        Assert.True(BeamParallel.Admitted(beam, out string? reason), reason);

        // Changed downstream subscription.
        EventHandler<EventArgs> downstream = (_, _) => { };
        SetEvent(value, "ValueChanged", downstream);
        Assert.False(BeamParallel.Admitted(beam, out reason)); Assert.Contains("downstream", reason);
        SetEvent(value, "ValueChanged", null);
        Assert.True(BeamParallel.Admitted(beam, out _));

        // Query-visible pull reader in the same part.
        var reader = Shell<BeamEmitter>(); Set(reader, "_effectsScaleFactor", value);
        f.AddComponent(value.Part!, reader);
        BeamParallel.ResetPullReaderCache(value);
        Assert.False(BeamParallel.Admitted(beam, out reason)); Assert.Contains("pull reader", reason);
        f.RemoveComponent(value.Part!, reader);
        BeamParallel.ResetPullReaderCache(value);
        var death = Shell<PartDeathEffects>(); Set(death, "_factorHitEffectsWith", value);
        f.AddComponent(value.Part!, death);
        Assert.True(BeamParallel.Admitted(beam, out reason), reason);

        // Outside the audited wiring.
        PartMultiValue other = f.MultiValue(beam, "cosmoteer.other_part", "EmitterValue");
        Subscribe(beam, other, "OnValueChanged");
        Assert.False(BeamParallel.Admitted(beam, out reason)); Assert.Contains("prism", reason);
        SetEvent(beam, "e_valueChanged", null);

        // Proxy listener: exact proxy, null downstream, chain ending in an exact emitter.
        BeamEmitterProxy proxy = f.Proxy(beam);
        Subscribe(beam, proxy, "OnEmitterValueChanged");
        Assert.True(BeamParallel.Admitted(beam, out reason), reason);
        SetEvent(proxy, "ValueChanged", downstream);
        Assert.False(BeamParallel.Admitted(beam, out reason));
        SetEvent(proxy, "ValueChanged", null);
        // Unknown proxy target and a cycle.
        Set(proxy._proxy, "<ProxiedComponent>k__BackingField", Shell<ForeignBeamEmitter>());
        Assert.False(BeamParallel.Admitted(beam, out reason)); Assert.Contains("chain", reason);
        BeamEmitterProxy loop = f.Proxy(beam); Set(loop._proxy, "<ProxiedComponent>k__BackingField", proxy);
        Set(proxy._proxy, "<ProxiedComponent>k__BackingField", loop);
        Assert.False(BeamParallel.ProxyChain(proxy));
        SetEvent(beam, "e_valueChanged", null);

        // Unknown listener and trigger subscriber.
        SetEvent(beam, "e_valueChanged", downstream);
        Assert.False(BeamParallel.Admitted(beam, out reason)); Assert.Contains("unknown value listener", reason);
        SetEvent(beam, "e_valueChanged", null);
        SetEvent(beam, "HitIntervalElapsed", new EventHandler<TriggerEventArgs>((_, _) => { }));
        Assert.False(BeamParallel.Admitted(beam, out reason)); Assert.Contains("trigger", reason);
    }

    [Fact]
    public void UnadmittedListenerAtValueEventInvalidatesTheRound()
    {
        using var f = new Setup("operational", beams: 2);
        using var session = f.Speculate(1);
        SetEvent(f.Beams[1], "e_valueChanged", new EventHandler<EventArgs>((_, _) => { }));
        HitInfo first = f.Take(out bool accepted, beam: 0);
        Assert.True(accepted, f.Reasons());
        first.Dispose();
        _ = f.Take(out accepted, beam: 1);
        Assert.False(accepted);
        Assert.False(session.Session.Valid);
    }

    /// <summary>An unaudited crew type (never constructed; shells only).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "ClassCannotBeInstantiated", Justification = "Only uninitialized shells are created.")]
    private sealed class ForeignCrew : SpaceCrew
    {
        private ForeignCrew() : base(null!, default, default) { }
    }

    /// <summary>An unaudited IBeamEmitter implementation (never constructed; shells only).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "ClassCannotBeInstantiated", Justification = "Only uninitialized shells are created.")]
    private sealed class ForeignBeamEmitter : BeamEmitter
    {
        private ForeignBeamEmitter() : base(null!) { }
    }

    // --- Harness -------------------------------------------------------------------------------------------------------

    private static void Subscribe(BeamEmitter beam, object target, string method)
    {
        var handler = AccessTools.Method(target.GetType(), method).CreateDelegate<EventHandler<EventArgs>>(target);
        var field = AccessTools.Field(typeof(BeamEmitter), "e_valueChanged");
        field.SetValue(beam, Delegate.Combine((Delegate?)field.GetValue(beam), handler));
    }
    private static void SetEvent(object instance, string name, Delegate? value) => Set(instance, name, value);

    internal sealed class Speculation(BeamParallel.Session session) : IDisposable
    {
        internal BeamParallel.Session Session { get; } = session;
        public void Dispose() { if (BeamParallel.Active == Session) BeamParallel.End(Session); }
    }

    private sealed class Setup : IDisposable
    {
        internal readonly SimRoot Sim = Shell<SimRoot>();
        internal readonly GameRoot Game = Shell<GameRoot>();
        internal readonly World World = new(new DynamicTreeBroadPhase<BodyFixtureProxy>());
        internal readonly Ship Source;
        internal readonly WeaponManager Manager = Shell<WeaponManager>();
        internal readonly List<BeamEmitter> Beams = [];
        internal readonly List<BeamEmitter> SecondBeams = [];
        internal readonly List<WeaponManager> Managers = [];
        private readonly string _scenario;
        internal (ulong, ulong) RandAfterNative;

        internal Setup(string scenario, int beams = 1, bool secondShip = false)
        {
            _scenario = scenario;
            BeamParallel.Stats.Reasons.Clear();
            Sim.SceneThread = Thread.CurrentThread; Sim.Game = Game; Game.NetManager = Shell<SPManager>();
            Set(Sim, "_sourcePlayerStack", new Stack<ActionSourcePlayer?>());
            var mode = Shell<CreativeSimModeManager>(); Set(Sim, "<Mode>k__BackingField", mode); Set(mode, "<Sim>k__BackingField", Sim);
            Set(Game, "<Mode>k__BackingField", Shell<CreativeGameModeManager>());
            var p0 = Shell<PlayerState>(); Set(p0, "<Team>k__BackingField", 0);
            var p1 = Shell<PlayerState>(); Set(p1, "<Team>k__BackingField", 1);
            Set(Game, "_playerStates", new SortedDictionary<int, PlayerState> { [0] = p0, [1] = p1 });
            Set(Sim, "<PhysicsWorld>k__BackingField", World);
            Source = NewShip(0, new V(0f, 0f), 11);
            Set(Source, "<Weapons>k__BackingField", Manager); Set(Manager, "<Ship>k__BackingField", Source);
            for (int i = 0; i < beams; i++) Beams.Add(NewBeam(i, Source));
            Set(Manager, "_emittingBeams", new List<BeamEmitter>(Beams));
            Managers.Add(Manager);
            if (secondShip)
            {
                Ship second = NewShip(0, new V(0f, -8f), 33);
                var manager = Shell<WeaponManager>();
                Set(second, "<Weapons>k__BackingField", manager); Set(manager, "<Ship>k__BackingField", second);
                SecondBeams.Add(NewBeam(0, second));
                Set(manager, "_emittingBeams", new List<BeamEmitter>(SecondBeams));
                Managers.Add(manager);
            }
            BuildTargets();
            World.ProcessChanges();
        }

        private Ship NewShip(int player, V location, ulong seed, Body? body = null)
        {
            var ship = Shell<Ship>();
            Set(ship, "_root", Sim); Set(ship, "p_scale", V.One);
            var metadata = Shell<ShipMetadata>(); Set(metadata, "<PlayerIndex>k__BackingField", player); Set(metadata, "<Ship>k__BackingField", ship);
            Set(ship, "<Metadata>k__BackingField", metadata);
            var parts = Shell<PartsManager>(); Set(parts, "<Ship>k__BackingField", ship);
            Set(parts, "_partsByCell", new Dictionary<IntVector2, Part>()); Set(parts, "_orderedParts", new List<Part>());
            Set(ship, "<Parts>k__BackingField", parts);
            body ??= World.CreateBody();
            body.SetTransform(location, 0f);
            body.UserData = ship;
            var physics = Shell<ShipPhysics>(); Set(physics, "<Ship>k__BackingField", ship);
            var wrapper = Shell<ScenePhysicsWrapper>(); Set(wrapper, "<Body>k__BackingField", body);
            Set(physics, "<PhysicsWrapper>k__BackingField", wrapper);
            Set(ship, "<Physics>k__BackingField", physics);
            Set(ship, "<Rand>k__BackingField", new FastRand(seed));
            return ship;
        }

        private static EffectFilter Filter(EffectFilter.Defaults defaults) => new(defaults);

        private BeamEmitter NewBeam(int index, Ship owner)
        {
            var rules = Shell<BeamEmitterRules>();
            var range = Shell<ModifiableFloat>(); range.BaseValue = 400f; Set(rules, "Range", range);
            Set(rules, "Width", 0.5f);
            Set(rules, "SecondaryFilter", Filter(EffectFilter.Defaults.Enemy));
            Set(rules, "ShieldCollisions", Filter(EffectFilter.Defaults.Enemy));
            if (IsCrew)
            {
                var crewFilter = Filter(EffectFilter.Defaults.Enemy); crewFilter.Enemies = true; // AffectsCrew: crew enter the operational ray
                Set(rules, "CrewCollisions", crewFilter);
            }
            else Set(rules, "CrewCollisions", Filter(EffectFilter.Defaults.CollisionsOff));
            Set(rules, "AttenuatorCollisions", Filter(EffectFilter.Defaults.Enemy));
            foreach (string flag in new[] { "FriendlyShipLowCollisions", "FriendlyShipHighCollisions", "NonFriendlyShipLowCollisions", "NonFriendlyShipHighCollisions" })
                Set(rules, flag, true);
            Set(rules, "PenetratesStructure", _scenario != "structural-blocking");
            Set(rules, "KeepLengthWhenHitNothing", _scenario == "keep-length");
            Set(rules, "AntiPhasingRaycastDistance", _scenario == "anti-phasing" ? 3f : 0f);
            var spreadMin = Shell<ModifiableAngle>(); var spreadMax = Shell<ModifiableAngle>();
            bool straight = _scenario == "chain-shield" || IsCrew; // beam i runs along world y = 0.5 + 2i
            spreadMin.BaseValue = straight ? 0f : -0.05f; spreadMax.BaseValue = straight ? 0f : 0.05f;
            Set(rules, "Spread", new Halfling.Range<ModifiableAngle>(spreadMin, spreadMax));
            if (_scenario == "offset")
            {
                var min = Shell<ModifiableFloat>(); min.BaseValue = 2f; var max = Shell<ModifiableFloat>(); max.BaseValue = 4f;
                Set(rules, "RandomOffsetRadius", new Halfling.Range<ModifiableFloat>(min, max));
            }
            if (_scenario is "secondary" or "crew-secondary")
            {
                var width = Shell<ModifiableFloat>(); width.BaseValue = 6f; Set(rules, "SecondaryWidth", width);
            }
            var partRules = Shell<PartRules>(); partRules.Size = new IntVector2(1, 1); Set(partRules, "TypeCategories", new HashSet<ID<PartCategory>>());
            var part = Shell<Part>(); Set(part, "<Rules>k__BackingField", partRules); Set(part, "p_ship", owner); Set(part, "_components", new List<PartComponent>());
            Set(part, "<Location>k__BackingField", new IntVector2(0, index * 2));
            var beam = Shell<BeamEmitter>();
            Set(beam, "<Rules>k__BackingField", rules); Set(beam, "<Part>k__BackingField", part);
            Set(beam, "_validateHit", AccessTools.Method(typeof(BeamEmitter), "ValidateHit").CreateDelegate<Func<Fixture, V, bool>>(beam));
            Set(beam, "<IsToggleOn>k__BackingField", true);
            beam._emissionTimeRemaining = (Time)1f;
            beam._effectsScale = 1f;
            beam._length = 50f;
            if (_scenario is "init-id" or "keep-length") { beam._hitStartTime = Time.Zero; beam._initialHitLength = 10f; }
            return beam;
        }

        private static void AddPart(Ship ship, IntVector2 cell, HealthType health)
        {
            var rules = Shell<PartRules>(); rules.Size = new IntVector2(1, 1); Set(rules, "HealthType", health); Set(rules, "TypeCategories", new HashSet<ID<PartCategory>>());
            var part = Shell<Part>(); Set(part, "<Rules>k__BackingField", rules); Set(part, "p_ship", ship); Set(part, "<Location>k__BackingField", cell);
            Set(part, "_components", new List<PartComponent>());
            ship.Parts._partsByCell[cell] = part;
        }

        private void BuildTargets()
        {
            // An enemy ship 100 units ahead: a row of structural cells, then operational cells behind them.
            Body body = World.CreateBody();
            body.UseWholeBodyBroadPhase = _scenario != "fixture-proxies";
            int player = _scenario == "unknown-player" ? 7 : 1;
            Ship target = NewShip(player, new V(100f, -10f), 22, body);
            if (_scenario == "miss" || _scenario == "keep-length") body.SetTransform(new V(100f, 500f), 0f);
            for (int y = 0; y < 24; y++)
            {
                for (int x = 0; x < 6; x++)
                {
                    HealthType health = x < 3 ? HealthType.Structural : HealthType.Operational;
                    AddPart(target, new IntVector2(x, y), health);
                }
            }
            if (_scenario == "chain")
            {
                var vertices = new Vertices { new(0f, 0f), new(0f, 24f), new(1f, 24f) };
                Fixture chain = body.CreateChainShape(vertices);
                chain.CollisionCategories = Category.Cat2;
                return;
            }
            if (_scenario is "shield" or "cold-shield" or "chain-shield") AddShield(target, body, warm: _scenario != "cold-shield");
            if (IsCrew)
            {
                target.Parts._orderedParts.Add(target.Parts._partsByCell[new IntVector2(0, 0)]); // home ship exists in the sim
                switch (_scenario)
                {
                    case "crew": AddCrew(new V(50f, 0.5f), 1f, home: target, player: 1); break;
                    case "crew-homeless": AddCrew(new V(50f, 0.5f), 1f, home: null, player: 1); break;
                    case "crew-friendly": AddCrew(new V(50f, 0.5f), 1f, home: null, player: 0); break;
                    case "crew-behind": AddCrew(new V(101.5f, 0.5f), 0.6f, home: target, player: 1); break; // past the structural row
                    case "crew-secondary": AddCrew(new V(10f, 2.5f), 0.3f, home: null, player: 1); break; // only the wide secondary ray reaches it
                    case "crew-type": AddCrew(new V(20f, 0.5f), 0.4f, home: target, player: 1, foreign: true); break; // beam 0 only
                }
            }
            if (_scenario == "chain-shield")
            {
                // A chain segment only in front of beam 0 (world y 0.5); beam 1 (y 2.5) passes it and hits the shield.
                Fixture chain = body.CreateChainShape(new Vertices { new(-20f, 10f), new(-20f, 11f), new(-19.5f, 11f) });
                chain.CollisionCategories = Category.Cat2;
            }
            void Rect(int left, int width, Category category)
            {
                Fixture fixture = body.CreateRectangle(width, 24f, 1f, new System.Numerics.Vector2(left + width / 2f, 12f));
                fixture.CollisionCategories = category;
                fixture.UserData = new ShipPhysics.CellsFixtureInfo(new IntRect(left, 0, width, 24), 1f, fixture);
            }
            Rect(0, 3, Category.Cat3 | Category.Cat4);
            Rect(3, 3, Category.Cat2 | Category.Cat4);
            if (_scenario == "init-id") Beams.ForEach(b => b._initHitObjectID = target.UniqueID);
        }

        internal ArcShield? Shield;
        internal SpaceCrew? Crew;
        private bool IsCrew => _scenario.StartsWith("crew", StringComparison.Ordinal);

        /// <summary>A flying crew as SpaceCrew.CreateBody builds it (SpaceCrew.cs:1123-1137): body and fixture user data, Cat10.</summary>
        private void AddCrew(V location, float radius, Ship? home, int player, bool foreign = false)
        {
            SpaceCrew crew = foreign ? Shell<ForeignCrew>() : Shell<SpaceCrew>();
            Set(crew, "_root", Sim);
            var soul = Shell<CrewSoul>(); Set(soul, "<HomeShip>k__BackingField", home); Set(soul, "<PlayerIndex>k__BackingField", player);
            Set(crew, "<Soul>k__BackingField", soul);
            Body body = World.CreateBody();
            body.SetTransform(location, 0f);
            body.UserData = crew;
            Fixture fixture = body.CreateCircle(radius, 1f);
            fixture.UserData = crew;
            fixture.CollisionCategories = Category.Cat10;
            Crew = crew;
        }

        /// <summary>A full-circle arc shield in front of the target, centred on its part component location.</summary>
        private void AddShield(Ship target, Body body, bool warm)
        {
            var rules = Shell<PartRules>(); rules.Size = new IntVector2(1, 1); Set(rules, "HealthType", HealthType.Operational); Set(rules, "TypeCategories", new HashSet<ID<PartCategory>>());
            var part = Shell<Part>(); Set(part, "<Rules>k__BackingField", rules); Set(part, "p_ship", target); Set(part, "<Location>k__BackingField", new IntVector2(-8, 12));
            Set(part, "_components", new List<PartComponent>());
            var shield = Shell<ArcShield>(); Set(shield, "<Part>k__BackingField", part); Set(shield, "<IsOperational>k__BackingField", true);
            Set(shield, "<RulesLocation>k__BackingField", new V(0.5f, 0.5f)); Set(shield, "<RulesRotation>k__BackingField", (Direction)MathF.PI);
            if (warm)
            {
                Set(shield, "_hasRulesLoc", true); Set(shield, "_hasRulesRot", true);
                Set(shield, "_cachedArcAngle", (Angle?)(Angle)6.2f); Set(shield, "_cachedArcRadius", (float?)6f);
            }
            V center = part.TransformPointFromRules(new V(0.5f, 0.5f));
            Fixture fixture = body.CreateCircle(6f, 1f, center);
            fixture.CollisionCategories = Category.Cat9;
            fixture.UserData = shield;
            Shield = shield;
        }

        internal Speculation Speculate(int workers)
        {
            BeamParallel.OverrideForTests((count, run) =>
            {
                var queue = new System.Collections.Concurrent.ConcurrentQueue<int>(Enumerable.Range(0, count));
                Thread[] threads = Enumerable.Range(0, workers).Select(_ => new Thread(() => { while (queue.TryDequeue(out int i)) run(i); })).ToArray();
                foreach (Thread t in threads) t.Start();
                foreach (Thread t in threads) t.Join();
            }, allowTestOwner: true);
            try
            {
                BeamParallel.Session s = BeamParallel.BeginWith(Sim, Managers) ?? throw new InvalidOperationException("no session: " + Reasons());
                return new Speculation(s);
            }
            finally { BeamParallel.OverrideForTests(null, allowTestOwner: true); }
        }

        internal (ulong, ulong) StateBeforeTake;
        internal HitInfo PatchedNative()
        {
            (V point, Direction direction, bool isInit) = Spread(Beams[0]);
            return Beams[0].GetHitInfo(point, direction, isInit);
        }
        internal (ulong, ulong) RandState() { Source.Rand.GetState(out ulong a, out ulong b); return (a, b); }

        /// <summary>DoEmitBeam lines 832-854 natively, then the GetHitInfo prefix.</summary>
        internal HitInfo Take(out bool accepted, int beam = 0) => Take(out accepted, Beams[beam]);
        internal HitInfo Take(out bool accepted, BeamEmitter b)
        {
            BeamParallel.EmitHook.BeforeValueEvent(b, 0);
            (V point, Direction direction, bool isInit) = Spread(b);
            StateBeforeTake = RandState();
            HitInfo result = default;
            accepted = !BeamParallel.TakeHook.Prefix(b, point, direction, isInit, ref result, out _);
            if (!accepted) { result.Dispose(); result = default; }
            BeamParallel.EmitHook.BeforeValueEvent(b, 1);
            return result;
        }
        private static (V, Direction, bool) Spread(BeamEmitter b)
        {
            V point = b.GetWorldSpawnLoc();
            Angle min = b.Rules.Spread.Min.GetValue(b.Part, b._effectsScale), max = b.Rules.Spread.Max.GetValue(b.Part, b._effectsScale);
            Direction direction = b.GetWorldSpawnDir() + b.Rand.Angle(min, max);
            return (point, direction, b.InitialHitLength <= 0f);
        }

        /// <summary>The unpatched native GetHitInfo for a beam from the current RNG state (restored afterwards).</summary>
        internal HitInfo Native(int beam = 0)
        {
            (ulong a, ulong b) = RandState();
            (V point, Direction direction, bool isInit) = Spread(Beams[beam]);
            HitInfo info = Beams[beam].GetHitInfo(point, direction, isInit);
            RandAfterNative = RandState();
            Source.Rand.SetState(a, b);
            return info;
        }

        internal string Reasons() => string.Join("; ", BeamParallel.Stats.Reasons.Select(kv => kv.Key + "=" + kv.Value));

        internal PartMultiValue MultiValue(BeamEmitter source, string partId, string componentId)
        {
            var partRules = Shell<PartRules>(); Set(partRules, "ID", new ID<PartRules>(partId));
            var part = Shell<Part>(); Set(part, "<Rules>k__BackingField", partRules); Set(part, "_components", new List<PartComponent>());
            var rules = Shell<PartMultiValueRules>(); Set(rules, "ID", new ID<PartComponentRules>(componentId));
            var value = Shell<PartMultiValue>(); Set(value, "<Rules>k__BackingField", rules); Set(value, "<Part>k__BackingField", part);
            Set(value, "_values", new IPartComponentValue[] { source });
            AddComponent(part, value);
            return value;
        }
        internal BeamEmitterProxy Proxy(BeamEmitter target)
        {
            var proxy = Shell<BeamEmitterProxy>();
            var handler = Shell<ProxyHandler<IBeamEmitter>>(); Set(handler, "<ProxiedComponent>k__BackingField", target);
            Set(proxy, "_proxy", handler);
            return proxy;
        }
        internal void AddComponent(Part part, PartComponent component) => part._components.Add(component);
        internal void RemoveComponent(Part part, PartComponent component) => part._components.Remove(component);

        public void Dispose()
        {
            if (BeamParallel.Active != null) BeamParallel.End(BeamParallel.Active);
            BeamParallel.OverrideForTests(null, allowTestOwner: false);
            World.Clear();
        }
    }

    private static void Set(object instance, string name, object? value)
    {
        for (Type? type = instance.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo? field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field == null) continue;
            field.SetValue(instance, value);
            return;
        }
        throw new MissingFieldException(instance.GetType().FullName, name);
    }
    private static T Shell<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
}
