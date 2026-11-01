using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Cosmoteer.Bullets;
using Cosmoteer.Bullets.Hits;
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
using Halfling.Scene2D;
using Halfling.Scene2D.Physics;
using Halfling.Timing;
using HarmonyLib;
using Xunit;
using HitInfo = Cosmoteer.Ships.Parts.Weapons.BeamEmitter.HitInfo;
using HitObject = Cosmoteer.Ships.Parts.Weapons.BeamEmitter.HitObject;
using V = Halfling.Geometry.Vector2;

namespace Performance.Tests;

/// <summary>
/// BeamFrozen (beam-design.md §9): the single-threaded oracle (every query serial, guards off) against the worker pipeline
/// (1/2/8 threads, shuffled partitions, scheduling jitter). Each run builds the same world from scratch and records a complete
/// per-tick transcript: every record's kind, inputs and query bits, the attenuation intents, the ordered apply payloads
/// (listeners, attenuations, StopEmission, DoHitEffect), every beam's state afterwards and every ship's RNG state.
/// </summary>
[Collection(nameof(BeamParallelTests))]
public sealed class BeamFrozenTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void ExplicitOptInOnly(string? value, bool expected) => Assert.Equal(expected, BeamParallel.IsRequested(value));

    [Fact]
    public void DeliveryTargetIsNotSharedGeneric()
    {
        Assert.False(SharedGenericRetarget.IsShared(BeamParallel.BucketTarget));
        Assert.False(BeamParallel.BucketTarget.DeclaringType!.IsGenericType);
        BeamParallel.ValidateNative(); // the mirrored native IL (DoEmitBeam, GetHitInfo, EmitBeams, the bucket loop) is pinned
    }

    private static readonly MethodInfo ParallelFor = AccessTools.Method(typeof(Halfling.Performance.FastParallel), nameof(Halfling.Performance.FastParallel.For));
    private static readonly MethodInfo ParallelWait = AccessTools.GetDeclaredMethods(typeof(Halfling.Performance.FastParallel))
        .Single(m => m.Name.Contains("g___WaitUntilFinished|", StringComparison.Ordinal));
    private static KnownLoaderPatches.Registration Reg(MethodBase target, string owner, KnownLoaderPatches.Kind kind, Type hookType, string hook) =>
        new(target, owner, kind, AccessTools.Method(hookType, hook));

    [Fact]
    public void OnlyTheBenchPhaseTimingRegistrationsAreExempt()
    {
        const string bench = BeamFrozen.BenchPhaseTimingOwner;
        Type timing = typeof(Bench.FramePhaseTiming);
        Assert.True(BeamFrozen.BenchPhaseTiming(Reg(ParallelFor, bench, KnownLoaderPatches.Kind.Prefix, timing, "ForPrefix")));
        Assert.True(BeamFrozen.BenchPhaseTiming(Reg(ParallelFor, bench, KnownLoaderPatches.Kind.Finalizer, timing, "ForFinalizer")));
        Assert.True(BeamFrozen.BenchPhaseTiming(Reg(ParallelWait, bench, KnownLoaderPatches.Kind.Prefix, timing, "WaitPrefix")));
        Assert.True(BeamFrozen.BenchPhaseTiming(Reg(ParallelWait, bench, KnownLoaderPatches.Kind.Finalizer, timing, "WaitFinalizer")));
        // Other owner, other hook method or type, other kind, other target: not exempt.
        Assert.False(BeamFrozen.BenchPhaseTiming(Reg(ParallelFor, "someone.else", KnownLoaderPatches.Kind.Prefix, timing, "ForPrefix")));
        Assert.False(BeamFrozen.BenchPhaseTiming(Reg(ParallelFor, bench, KnownLoaderPatches.Kind.Prefix, timing, "WaitPrefix")));
        Assert.False(BeamFrozen.BenchPhaseTiming(Reg(ParallelFor, bench, KnownLoaderPatches.Kind.Prefix, typeof(ForeignTiming), "ForPrefix")));
        Assert.False(BeamFrozen.BenchPhaseTiming(Reg(ParallelFor, bench, KnownLoaderPatches.Kind.Postfix, timing, "ForPrefix")));
        Assert.False(BeamFrozen.BenchPhaseTiming(Reg(ParallelFor, bench, KnownLoaderPatches.Kind.Transpiler, timing, "ForPrefix")));
        Assert.False(BeamFrozen.BenchPhaseTiming(Reg(BeamParallel.BucketTarget, bench, KnownLoaderPatches.Kind.Prefix, timing, "ForPrefix")));
        Assert.False(BeamFrozen.BenchPhaseTiming(Reg(AccessTools.Method(typeof(World), "RayCast"), bench, KnownLoaderPatches.Kind.Prefix, timing, "ForPrefix")));
    }

    /// <summary>Live Harmony registrations on FastParallel.For through the foreign-patch scan: the bench pair keeps frozen on,
    /// any other hook there turns it off. (Only FastParallel registrations are scanned: other test classes patch the game.)</summary>
    [Fact]
    public void ForeignDispatcherPatchTurnsFrozenOff()
    {
        static string? Scan() => BeamFrozen.FirstForeign(
            HarmonyPatchIdentity.ReadRuntimeView().Registrations.Where(r => r.Target.DeclaringType == typeof(Halfling.Performance.FastParallel)), _ => false);
        var bench = new Harmony(BeamFrozen.BenchPhaseTimingOwner);
        var foreign = new Harmony("Performance.Tests.ForeignDispatcher");
        try
        {
            bench.Patch(ParallelFor, prefix: new HarmonyMethod(typeof(Bench.FramePhaseTiming), "ForPrefix"),
                finalizer: new HarmonyMethod(typeof(Bench.FramePhaseTiming), "ForFinalizer"));
            bench.Patch(ParallelWait, prefix: new HarmonyMethod(typeof(Bench.FramePhaseTiming), "WaitPrefix"),
                finalizer: new HarmonyMethod(typeof(Bench.FramePhaseTiming), "WaitFinalizer"));
            Assert.Equal(4, HarmonyPatchIdentity.ReadRuntimeView().Registrations.Count(r => r.Owner == bench.Id));
            Assert.Null(Scan());
            foreign.Patch(ParallelFor, postfix: new HarmonyMethod(typeof(ForeignTiming), "ForPrefix"));
            Assert.Contains("Performance.Tests.ForeignDispatcher", Scan());
            foreign.UnpatchAll(foreign.Id);
            Assert.Null(Scan());
            bench.Patch(ParallelFor, postfix: new HarmonyMethod(typeof(Bench.FramePhaseTiming), "ForPrefix")); // bench owner, wrong kind
            Assert.NotNull(Scan());
        }
        finally { foreign.UnpatchAll(foreign.Id); bench.UnpatchAll(bench.Id); }
        Assert.Null(Scan());
    }

    private static class ForeignTiming
    {
        public static void ForPrefix() { }
    }

    [Theory]
    [InlineData(typeof(MPHostManager))]
    [InlineData(typeof(MPClientManager))]
    public void MultiplayerSimStaysNative(Type netManager)
    {
        SimRoot sim = Shell<SimRoot>();
        var game = Shell<GameRoot>();
        game.NetManager = (NetManager)RuntimeHelpers.GetUninitializedObject(netManager);
        sim.Game = game;
        Assert.False(BeamParallel.SinglePlayer(sim));
        BeamFrozen.Binding b = BeamFrozen.Bind(sim);
        Assert.False(b.On);
        Assert.False(b.Workers);
        Assert.Equal("multiplayer", b.Reason);
        Assert.Same(b, BeamFrozen.Bind(sim)); // decided once per sim
        game.NetManager = Shell<SPManager>();
        Assert.True(BeamParallel.SinglePlayer(sim));
        Assert.Same(b, BeamFrozen.Bind(sim)); // a later net manager change does not rebind
    }

    public static IEnumerable<object[]> Partitions()
    {
        foreach (int workers in new[] { 1, 2, 8 })
            foreach (int seed in new[] { 1, 2, 3 })
                yield return [workers, seed];
    }

    /// <summary>The whole mixed tick (shields, crew, penetration, secondary, offsets, shared attenuators with deaths, a cold
    /// shield and a chain shape on the serial path, equal-distance fixtures, a stop, a toggled-off beam) equals the oracle.</summary>
    [Theory]
    [MemberData(nameof(Partitions))]
    public void WorkersMatchOracleBitForBit(int workers, int seed)
    {
        string oracle = Run(BeamFrozen.QueryMode.Oracle, 0, 0, out Counts o);
        string parallel = Run(BeamFrozen.QueryMode.Workers, workers, seed, out Counts p);
        Assert.Equal(oracle, parallel);
        Assert.True(p.WorkerReady > 0, "workers computed nothing");
        Assert.True(p.Serial > 0 && p.Serial < o.Serial, $"serial {p.Serial} oracle {o.Serial}");
        Assert.True(o.Attenuations > 0 && o.Discarded > 0 && o.Dropped > 0, $"att {o.Attenuations} discarded {o.Discarded} dropped {o.Dropped}");
    }

    [Fact]
    public void RepeatedRunsAreIdentical()
    {
        string a = Run(BeamFrozen.QueryMode.Workers, 8, 7, out _);
        string b = Run(BeamFrozen.QueryMode.Workers, 8, 8, out _);
        Assert.Equal(a, b);
    }

    /// <summary>All beam draws happen in the preparation phase, per ship in native order: spread angle, then the offset point.</summary>
    [Fact]
    public void RngIsDrawnInNativeOrderDuringPreparation()
    {
        using var w = new FrozenWorld();
        var expected = w.Sources.Select(ship => { ship.Rand.GetState(out ulong a, out ulong b); return new FastRand(0uL, 0uL).Also(r => r.SetState(a, b)); }).ToList();
        for (int k = 0; k < w.Sources.Count; k++)
            foreach (BeamEmitter beam in w.Managers[k]._emittingBeams)
            {
                if (!beam.IsToggleOn || beam._emissionTimeRemaining <= 0f) continue;
                Angle min = beam.Rules.Spread.Min.GetValue(beam.Part, 1f), max = beam.Rules.Spread.Max.GetValue(beam.Part, 1f);
                _ = expected[k].Angle(min, max);
                if (beam.Rules.RandomOffsetRadius is { } radius)
                {
                    float range = beam.GetCurrentRange();
                    _ = expected[k].PointInside(new Circle(V.Zero, Halfling.Mathx.Lerp(radius.Min.GetValue(beam.Part, 1f), radius.Max.GetValue(beam.Part, 1f), Halfling.Mathx.InverseLerp(0f, range, range))));
                }
            }
        w.Configure(BeamFrozen.QueryMode.Workers, 2, 1);
        BeamFrozen.Session s = BeamFrozen.PrepareAndQuery(w.Sim, w.Binding, w.Members, w.Members.Length);
        try
        {
            for (int k = 0; k < w.Sources.Count; k++)
            {
                expected[k].GetState(out ulong ea, out ulong eb);
                w.Sources[k].Rand.GetState(out ulong a, out ulong b);
                Assert.Equal((ea, eb), (a, b));
            }
        }
        finally { BeamFrozen.End(s); }
    }

    /// <summary>A listener of an earlier beam changes a later beam's range, stops another and adds a new emitter: the later
    /// query keeps bucket-start inputs, the stopped emitter is cancelled, the added one waits for the next tick.</summary>
    [Fact]
    public void ListenersAffectLaterBeamsOnlyThroughApplication()
    {
        using var w = new FrozenWorld();
        BeamEmitter first = w.Managers[0]._emittingBeams[0], second = w.Managers[0]._emittingBeams[1], third = w.Managers[0]._emittingBeams[2];
        BeamEmitter added = w.SpareBeam;
        float rangeBefore = second.Rules.Range.BaseValue;
        int fires = 0;
        Subscribe(first, (_, _) =>
        {
            if (fires++ > 0) return;
            second.Rules.Range.BaseValue = 1f; // would shorten a re-queried beam to 1
            third._emissionTimeRemaining = null; Set(third, "<IsToggleOn>k__BackingField", false); // stopped by an earlier application
            w.Managers[0]._emittingBeams.Add(added);
        });
        long cancelled = BeamFrozen.Stats.Cancelled;
        string log = w.RunTick(BeamFrozen.QueryMode.Workers, 2, 1);
        Assert.Equal(cancelled + 1, BeamFrozen.Stats.Cancelled);
        Assert.True(second._length > 1f, "bucket-start range used: " + second._length);
        Assert.DoesNotContain("listen " + w.Name(added), log);
        Assert.Contains("listen " + w.Name(third), log); // BE:832 still runs before the cancel check
        Assert.DoesNotContain("hit " + w.Name(third), log);
        Assert.Equal(1f, second.Rules.Range.BaseValue);
        second.Rules.Range.BaseValue = rangeBefore;
    }

    /// <summary>A prism-style proxy chain listens to beam 0 (BE:832 and BE:948 both forward through it); its downstream
    /// subscriber splits a later beam's part onto another ship during application: that emitter is cancelled.</summary>
    [Fact]
    public void ProxyChainListenerAndSplitDuringApply()
    {
        using var w = new FrozenWorld();
        BeamEmitter source = w.Managers[0]._emittingBeams[0], split = w.Managers[0]._emittingBeams[3];
        var proxy = Shell<BeamEmitterProxy>();
        var handler = Shell<ProxyHandler<IBeamEmitter>>(); Set(handler, "<ProxiedComponent>k__BackingField", source);
        Set(proxy, "_proxy", handler);
        Subscribe(source, AccessTools.Method(typeof(BeamEmitterProxy), "OnEmitterValueChanged").CreateDelegate<EventHandler<EventArgs>>(proxy));
        int forwarded = 0;
        Set(proxy, "ValueChanged", new EventHandler<EventArgs>((_, _) =>
        {
            w.Log.Append("proxy\n");
            if (forwarded++ == 0) Set(split.Part!, "p_ship", w.Sources[1]); // ship split: the part now belongs to another ship
        }));
        long cancelled = BeamFrozen.Stats.Cancelled;
        string log = w.RunTick(BeamFrozen.QueryMode.Workers, 8, 4);
        Assert.Equal(2, forwarded);
        string[] lines = log.Split('\n');
        int first = Array.IndexOf(lines, "proxy"), last = Array.LastIndexOf(lines, "proxy");
        Assert.True(first < Array.FindIndex(lines, l => l.StartsWith("hit B0 ", StringComparison.Ordinal)) &&
            last > Array.FindLastIndex(lines, l => l.StartsWith("hit B0 ", StringComparison.Ordinal)), "proxy forwards BE:832 before and BE:948 after B0's effects");
        Assert.Equal(cancelled + 1, BeamFrozen.Stats.Cancelled);
        Assert.DoesNotContain(lines, l => l.StartsWith("hit B3 ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("rec 3 B3 Emit", StringComparison.Ordinal)); // its frozen query existed and was discarded
    }

    /// <summary>Listener order: per Emit beam BE:832 then BE:948, per ListenOnly beam BE:832 only, beams in native order; a Stop
    /// beam's StopEmission (with its own BE:823 event) runs in its slot.</summary>
    [Fact]
    public void ApplyRunsListenersStopAndEffectsInNativeOrder()
    {
        using var w = new FrozenWorld();
        string log = w.RunTick(BeamFrozen.QueryMode.Workers, 8, 3);
        string[] lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var listens = lines.Where(l => l.StartsWith("listen ", StringComparison.Ordinal)).Select(l => l["listen ".Length..]).ToList();
        var expected = new List<string>();
        foreach (WeaponManager m in w.Managers)
            foreach (BeamEmitter b in w.Initial[m])
            {
                expected.Add(w.Name(b));
                if (w.Kinds[b] == "emit" || w.Kinds[b] == "stop") expected.Add(w.Name(b)); // BE:948, or StopEmission's BE:823
            }
        Assert.Equal(expected, listens);
        Assert.Contains(lines, l => l.StartsWith("stop " + w.Name(w.Stopping), StringComparison.Ordinal));
        Assert.DoesNotContain(w.Managers.SelectMany(m => m._emittingBeams), b => ReferenceEquals(b, w.Stopping));
        // Every hit line sits between its own beam's two listener calls.
        string? current = null;
        foreach (string l in lines)
        {
            if (l.StartsWith("listen ", StringComparison.Ordinal)) current = l["listen ".Length..];
            else if (l.StartsWith("hit ", StringComparison.Ordinal)) Assert.Equal(current, l.Split(' ')[1]);
        }
    }

    /// <summary>Shared attenuator: the pool is consumed in apply order, the death runs its immediate effect in place (it removes
    /// a part another beam hit), later attenuations of the dead bullet are discarded and the removed part's hit is dropped.</summary>
    [Fact]
    public void SharedAttenuatorDeathAndDestroyedTargets()
    {
        using var w = new FrozenWorld();
        long discarded = BeamFrozen.Stats.DiscardedAttenuations, dropped = BeamFrozen.Stats.DroppedTargets;
        string log = w.RunTick(BeamFrozen.QueryMode.Workers, 2, 5);
        string[] lines = log.Split('\n');
        // Both B5 and B6 met the shared bullet in their frozen queries ...
        Assert.Contains(lines, l => l.StartsWith("rec 5 B5 ", StringComparison.Ordinal) && l.Contains(" att shared-att ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("rec 6 B6 ", StringComparison.Ordinal) && l.Contains(" att shared-att ", StringComparison.Ordinal));
        // ... B5 (applied first) drains the pool and the death runs in place; B6's attenuation of the dead bullet is discarded.
        var attenuations = lines.Where(l => l.StartsWith("attenuate ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["attenuate shared-att 00000000,41200000 " + lines.First(l => l.StartsWith("rec 5 ", StringComparison.Ordinal)).Split(" att shared-att 00000000,41200000 ")[1]], attenuations);
        int death = Array.IndexOf(lines, "death " + w.Name(w.SharedBullet));
        Assert.Equal(Array.IndexOf(lines, attenuations[0]) + 1, death);
        Assert.True(death < Array.FindIndex(lines, l => l.StartsWith("hit B6 ", StringComparison.Ordinal)));
        Assert.True(BeamFrozen.Stats.DiscardedAttenuations > discarded);
        Assert.True(BeamFrozen.Stats.DroppedTargets > dropped);
        Assert.DoesNotContain(" " + w.Name(w.DoomedPart) + " ", log.Split("death ")[1]); // no hit effect on the removed part after the death
    }

    /// <summary>Cold shield and chain shape: workers stop before touching them; the serial path warms the cache natively.</summary>
    [Fact]
    public void UnauditedContentRunsTheSerialPathOnTheMainThread()
    {
        using var w = new FrozenWorld();
        Assert.False(w.ColdShield._cachedArcRadius.HasValue);
        long serial = BeamFrozen.Stats.Serial;
        w.RunTick(BeamFrozen.QueryMode.Workers, 8, 2);
        Assert.True(BeamFrozen.Stats.Serial > serial);
        Assert.Contains(BeamFrozen.Stats.Stops.Keys, k => k == "cold shield");
        Assert.Contains(BeamFrozen.Stats.Stops.Keys, k => k == "shape");
        Assert.True(w.ColdShield._cachedArcRadius.HasValue); // warmed by the serial query, never on a worker
    }

    // --- Transcript ----------------------------------------------------------------------------------------------------

    internal readonly record struct Counts(long WorkerReady, long Serial, long Attenuations, long Discarded, long Dropped);

    private static string Run(BeamFrozen.QueryMode mode, int workers, int seed, out Counts counts)
    {
        using var w = new FrozenWorld();
        long ready = BeamFrozen.Stats.WorkerReady, serial = BeamFrozen.Stats.Serial, att = BeamFrozen.Stats.Attenuations,
            disc = BeamFrozen.Stats.DiscardedAttenuations, drop = BeamFrozen.Stats.DroppedTargets;
        string log = w.RunTick(mode, workers, seed);
        counts = new Counts(BeamFrozen.Stats.WorkerReady - ready, BeamFrozen.Stats.Serial - serial, BeamFrozen.Stats.Attenuations - att,
            BeamFrozen.Stats.DiscardedAttenuations - disc, BeamFrozen.Stats.DroppedTargets - drop);
        return log;
    }

    private static void Subscribe(BeamEmitter beam, EventHandler<EventArgs> handler)
    {
        FieldInfo field = AccessTools.Field(typeof(BeamEmitter), "e_valueChanged");
        field.SetValue(beam, Delegate.Combine((Delegate?)field.GetValue(beam), handler));
    }

    internal static class Hooks
    {
        internal static FrozenWorld? Active;
        public static bool DoHitEffect(BeamEmitter __instance, Part? hitPart, ArcShield? hitShield, SpaceCrew? hitCrew, BeamEmitterRules.BeamHitRules? hitRules,
            V worldPoint, Direction worldRot, V hitNormal, int structuralHitNumber)
        {
            FrozenWorld w = Active!;
            w.Log.Append(CultureInfo.InvariantCulture, $"hit {w.Name(__instance)} {w.Name(hitPart)} {w.Name(hitShield)} {w.Name(hitCrew)} {w.Name(hitRules)} {Bits(worldPoint)} {Bits((float)worldRot)} {Bits(hitNormal)} #{structuralHitNumber}\n");
            return false;
        }
        public static bool UpdateMediaNodes() => false;
        public static bool AttenuateBeam(BulletVolumeHit __instance, Segment beamSegment, int beamStrength, ref int __result)
        {
            FrozenWorld w = Active!;
            w.Log.Append(CultureInfo.InvariantCulture, $"attenuate {w.Name(__instance)} {Bits(beamSegment.Point1)} {Bits(beamSegment.Point2)} {beamStrength}\n");
            int pool = w.Pools[__instance] -= beamStrength;
            __result = 0;
            if (pool <= 0 && !__instance.Bullet.IsMarkedForDestruction)
            {
                Set(__instance.Bullet, "<MarkedForDestructionCounter>k__BackingField", 1);
                w.Log.Append("death " + w.Name(__instance.Bullet) + "\n");
                w.OnDeath(__instance); // the immediate OnDeath effect (BaseBulletDeath.cs:71-86)
            }
            return false;
        }
        public static void StopEmission(BeamEmitter __instance) => Active!.Log.Append("stop " + Active.Name(__instance) + "\n");
    }

    internal static string Bits(float f) => BitConverter.SingleToInt32Bits(f).ToString("X8", CultureInfo.InvariantCulture);
    internal static string Bits(V v) => Bits(v.X) + "," + Bits(v.Y);

    // --- World ---------------------------------------------------------------------------------------------------------

    internal sealed class FrozenWorld : IDisposable
    {
        internal readonly SimRoot Sim = Shell<SimRoot>();
        internal readonly GameRoot Game = Shell<GameRoot>();
        internal readonly World World = new(new DynamicTreeBroadPhase<BodyFixtureProxy>());
        internal readonly List<Ship> Sources = [];
        internal readonly List<WeaponManager> Managers = [];
        internal readonly Dictionary<WeaponManager, List<BeamEmitter>> Initial = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<BeamEmitter, string> Kinds = new(ReferenceEqualityComparer.Instance);
        internal IFixedUpdateableSceneObject[] Members = [];
        internal readonly BeamFrozen.Binding Binding = new() { On = true, Workers = true };
        internal readonly StringBuilder Log = new();
        internal readonly Dictionary<BulletVolumeHit, int> Pools = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, string> _names = new(ReferenceEqualityComparer.Instance);
        private readonly Harmony _harmony = new("Performance.Tests.BeamFrozen");
        internal ArcShield ColdShield = null!;
        internal Bullet SharedBullet = null!;
        internal Part DoomedPart = null!;
        internal BeamEmitter Stopping = null!;
        internal BeamEmitter SpareBeam = null!;
        private readonly BeamEmitterRules.BeamHitRules[] _hitRules = new BeamEmitterRules.BeamHitRules[6];

        internal FrozenWorld()
        {
            Sim.SceneThread = Thread.CurrentThread; Sim.Game = Game; Game.NetManager = Shell<SPManager>();
            Set(Sim, "_sourcePlayerStack", new Stack<ActionSourcePlayer?>());
            var mode = Shell<CreativeSimModeManager>(); Set(Sim, "<Mode>k__BackingField", mode); Set(mode, "<Sim>k__BackingField", Sim);
            Set(Game, "<Mode>k__BackingField", Shell<CreativeGameModeManager>());
            var p0 = Shell<PlayerState>(); Set(p0, "<Team>k__BackingField", 0);
            var p1 = Shell<PlayerState>(); Set(p1, "<Team>k__BackingField", 1);
            Set(Game, "_playerStates", new SortedDictionary<int, PlayerState> { [0] = p0, [1] = p1 });
            Set(Sim, "<PhysicsWorld>k__BackingField", World);
            var updater = Shell<FixedUpdater>(); updater.Interval = (Time)(1f / 30f);
            Set(Sim, "<FixedUpdater>k__BackingField", updater);
            string[] kinds = ["operational", "structural", "shield", "crew", "attenuator", "nothing"];
            for (int i = 0; i < 6; i++) { _hitRules[i] = Shell<BeamEmitterRules.BeamHitRules>(); _names[_hitRules[i]] = kinds[i]; }
            BuildTargets();
            // Source ships: 3 ships x 6 beams, rows y = 0.5 + 2i (+ ship offset); mixed rule shapes.
            for (int k = 0; k < 3; k++)
            {
                Ship ship = NewShip(0, new V(0f, k * 12f), 11uL + (ulong)k, "S" + k);
                var manager = Shell<WeaponManager>();
                Set(ship, "<Weapons>k__BackingField", manager); Set(manager, "<Ship>k__BackingField", ship);
                var beams = new List<BeamEmitter>();
                for (int i = 0; i < 6; i++) beams.Add(NewBeam(ship, k * 6 + i, i));
                Set(manager, "_emittingBeams", beams);
                var proxy = Shell<CustomFixedUpdateProxy>();
                FieldInfo callback = AccessTools.Field(typeof(CustomFixedUpdateProxy), "_callback");
                Set(proxy, "_callback", AccessTools.Method(typeof(WeaponManager), "EmitBeams").CreateDelegate(callback.FieldType, manager));
                // Scene plumbing of an enabled proxy (RefreshEmitBeamsEnabled after StopEmission).
                object fields = RuntimeHelpers.GetUninitializedObject(typeof(SceneComponent).GetNestedType("FixedUpdateFields", BindingFlags.NonPublic)!);
                Set(fields, "_fixedUpdatingEnabled", true);
                Set(proxy, "_fuFields", fields);
                Set(manager, "_emitBeamsProxy", proxy);
                Sources.Add(ship); Managers.Add(manager); Initial[manager] = [.. beams];
            }
            Stopping = Managers[1]._emittingBeams[4];
            Stopping._emissionTimeRemaining = (Time)0f;
            Kinds[Stopping] = "stop";
            BeamEmitter off = Managers[2]._emittingBeams[5];
            Set(off, "<IsToggleOn>k__BackingField", false);
            Kinds[off] = "listen";
            SpareBeam = NewBeam(Sources[0], 99, 0);
            Members = [.. Managers.Select(m => (IFixedUpdateableSceneObject)m._emitBeamsProxy)];
            World.ProcessChanges();
            foreach (BeamEmitter beam in Managers.SelectMany(m => m._emittingBeams)) Subscribe(beam, (sender, _) => Log.Append("listen " + Name(sender) + "\n"));
            _harmony.Patch(AccessTools.Method(typeof(BeamEmitter), "DoHitEffect"), prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.DoHitEffect)));
            _harmony.Patch(AccessTools.Method(typeof(BeamEmitter), "UpdateMediaNodes"), prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.UpdateMediaNodes)));
            _harmony.Patch(AccessTools.Method(typeof(BulletVolumeHit), "AttenuateBeam"), prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.AttenuateBeam)));
            _harmony.Patch(AccessTools.Method(typeof(BeamEmitter), "StopEmission"), prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.StopEmission)));
            Hooks.Active = this;
        }

        internal string Name(object? o) => o == null ? "-" : _names.TryGetValue(o, out string? n) ? n : o.GetType().Name + "?";

        internal void Configure(BeamFrozen.QueryMode mode, int workers, int seed)
        {
            Action<int, Action<int>>? dispatch = null;
            if (mode == BeamFrozen.QueryMode.Workers)
                dispatch = (count, run) =>
                {
                    // Shuffled partition, uneven thread start and per-item jitter.
                    var rng = new Random(seed);
                    int[] order = Enumerable.Range(0, count).OrderBy(_ => rng.Next()).ToArray();
                    int next = -1;
                    Thread[] threads = Enumerable.Range(0, workers).Select(t => new Thread(() =>
                    {
                        var jitter = new Random(seed * 31 + t);
                        Thread.Sleep(jitter.Next(0, 3));
                        int i;
                        while ((i = Interlocked.Increment(ref next)) < count)
                        {
                            Thread.SpinWait(jitter.Next(0, 20000));
                            run(order[i]);
                        }
                    })).ToArray();
                    foreach (Thread t in threads) t.Start();
                    foreach (Thread t in threads) t.Join();
                };
            BeamFrozen.OverrideForTests(dispatch, mode, allowTestOwner: true);
        }

        /// <summary>One full bucket -4 (prepare, query, apply) and the complete transcript.</summary>
        internal string RunTick(BeamFrozen.QueryMode mode, int workers, int seed)
        {
            Configure(mode, workers, seed);
            BeamFrozen.AfterQueryForTests = s =>
            {
                foreach (BeamFrozen.FrozenRecord r in s.Records) Log.Append(Describe(r)).Append('\n');
            };
            try { BeamFrozen.RunBucket(Sim, Binding, Members, Members.Length); }
            finally { BeamFrozen.AfterQueryForTests = null; BeamFrozen.OverrideForTests(null, BeamFrozen.QueryMode.Workers, allowTestOwner: false); }
            foreach (BeamEmitter b in Initial.Values.SelectMany(x => x))
                Log.Append(CultureInfo.InvariantCulture, $"state {Name(b)} len {Bits(b._length)} init {Bits(b._initialHitLength)} id {b._initHitObjectID} dmg {(float)b._damageTimeRemaining} emit {(float?)b._emissionTimeRemaining} seg {Bits(b._effectSeg.Point1)} {Bits(b._effectSeg.Point2)} toggle {b.IsToggleOn} scale {Bits(b._effectsScale)}\n");
            foreach (Ship ship in Sources) { ship.Rand.GetState(out ulong a, out ulong c); Log.Append(CultureInfo.InvariantCulture, $"rand {Name(ship)} {a:X16} {c:X16}\n"); }
            return Log.ToString();
        }

        private string Describe(BeamFrozen.FrozenRecord r)
        {
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"rec {r.Order} {Name(r.Emitter)} {r.Kind}");
            if (r.Kind != BeamFrozen.Kind.Emit) return sb.ToString();
            HitInfo h = r.Info;
            sb.Append(CultureInfo.InvariantCulture, $" spawn {Bits(r.Spawn)} dir {Bits((float)r.Dir)} end {Bits(r.EndPoint)} | len {Bits(h.Length)} end {Bits(h.EndPoint)} op {Bits(h.Operational.WorldPoint)} {Bits(h.Operational.WorldNormal)} {Hit(h.Operational.Hit)}");
            sb.Append(CultureInfo.InvariantCulture, $" st {Name(h.Structural.Part)} {Bits(h.Structural.WorldPoint)} {Bits(h.Structural.WorldNormal)}");
            if (h.AllStructuralHits != null) foreach (var s in h.AllStructuralHits) sb.Append(CultureInfo.InvariantCulture, $" [{Name(s.Part)} {Bits(s.WorldPoint)} {Bits(s.WorldNormal)}]");
            if (r.Attenuator != null) sb.Append(CultureInfo.InvariantCulture, $" att {Name(r.Attenuator)} {Bits(r.AttenuationSegment.Point1)} {Bits(r.AttenuationSegment.Point2)} {r.AttenuationLimit}");
            return sb.ToString();
        }
        private string Hit(HitObject o) => $"{Name(o.Part)}/{Name(o.Collider)}/{Name(o.Shield)}/{Name(o.Crew)}/{Name(o.Attenuator)}";

        // --- Content --------------------------------------------------------------------------------------------------

        private Ship NewShip(int player, V location, ulong seed, string name, Body? body = null)
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
            _names[ship] = name;
            return ship;
        }

        private static EffectFilter Filter(EffectFilter.Defaults defaults) => new(defaults);

        /// <summary>Beam variant by slot: 0 plain penetrating, 1 secondary, 2 offset, 3 non-penetrating, 4 crew-capable, 5 attenuating.</summary>
        private BeamEmitter NewBeam(Ship owner, int index, int variant)
        {
            var rules = Shell<BeamEmitterRules>();
            var range = Shell<ModifiableFloat>(); range.BaseValue = 400f; Set(rules, "Range", range);
            Set(rules, "Width", index == 12 ? 0f : 0.5f); // B12 crosses the chain segment: ChainShape.RayCast needs radius 0 (native throws otherwise)
            Set(rules, "SecondaryFilter", Filter(EffectFilter.Defaults.Enemy));
            Set(rules, "ShieldCollisions", Filter(EffectFilter.Defaults.Enemy));
            var crew = Filter(EffectFilter.Defaults.Enemy);
            if (variant == 4) crew.Enemies = true; else crew = Filter(EffectFilter.Defaults.CollisionsOff);
            Set(rules, "CrewCollisions", crew);
            var attenuators = Filter(EffectFilter.Defaults.Enemy); attenuators.Bullets = true;
            Set(rules, "AttenuatorCollisions", attenuators);
            foreach (string flag in new[] { "FriendlyShipLowCollisions", "FriendlyShipHighCollisions", "NonFriendlyShipLowCollisions", "NonFriendlyShipHighCollisions" })
                Set(rules, flag, true);
            Set(rules, "PenetratesStructure", variant != 3);
            var spreadMin = Shell<ModifiableAngle>(); var spreadMax = Shell<ModifiableAngle>();
            spreadMin.BaseValue = -0.005f; spreadMax.BaseValue = 0.005f;
            Set(rules, "Spread", new Halfling.Range<ModifiableAngle>(spreadMin, spreadMax));
            if (variant == 2)
            {
                var min = Shell<ModifiableFloat>(); min.BaseValue = 1f; var max = Shell<ModifiableFloat>(); max.BaseValue = 3f;
                Set(rules, "RandomOffsetRadius", new Halfling.Range<ModifiableFloat>(min, max));
            }
            if (variant == 1 && index < 12) { var width = Shell<ModifiableFloat>(); width.BaseValue = 6f; Set(rules, "SecondaryWidth", width); }
            if (variant is 0 or 5) { var limit = Shell<ModifiableInt>(); limit.BaseValue = 40; Set(rules, "AttenuationLimit", limit); }
            ModifiableTime interval = 0.25f;
            Set(rules, "HitInterval", interval);
            Set(rules, "HitOperational", _hitRules[0]); Set(rules, "HitStructural", _hitRules[1]); Set(rules, "HitShield", _hitRules[2]);
            Set(rules, "HitCrew", _hitRules[3]); Set(rules, "HitAttenuator", _hitRules[4]); Set(rules, "HitNothing", _hitRules[5]);
            var partRules = Shell<PartRules>(); partRules.Size = new IntVector2(1, 1); Set(partRules, "TypeCategories", new HashSet<ID<PartCategory>>());
            var part = Shell<Part>(); Set(part, "<Rules>k__BackingField", partRules); Set(part, "p_ship", owner); Set(part, "_components", new List<PartComponent>());
            Set(part, "<Location>k__BackingField", new IntVector2(0, (index % 6) * 2));
            var beam = Shell<BeamEmitter>();
            Set(beam, "<Rules>k__BackingField", rules); Set(beam, "<Part>k__BackingField", part);
            Set(beam, "_validateHit", AccessTools.Method(typeof(BeamEmitter), "ValidateHit").CreateDelegate<Func<Fixture, V, bool>>(beam));
            Set(beam, "<IsToggleOn>k__BackingField", true);
            beam._emissionTimeRemaining = (Time)1f;
            beam._effectsScale = 1f;
            beam._length = 50f;
            _names[beam] = "B" + index;
            _names[part] = "BP" + index;
            Kinds[beam] = "emit";
            return beam;
        }

        private void AddPart(Ship ship, IntVector2 cell, HealthType health)
        {
            var rules = Shell<PartRules>(); rules.Size = new IntVector2(1, 1); Set(rules, "HealthType", health); Set(rules, "TypeCategories", new HashSet<ID<PartCategory>>());
            var part = Shell<Part>(); Set(part, "<Rules>k__BackingField", rules); Set(part, "p_ship", ship); Set(part, "<Location>k__BackingField", cell);
            Set(part, "_components", new List<PartComponent>());
            ship.Parts._partsByCell[cell] = part;
            _names[part] = Name(ship) + "@" + cell.X + "," + cell.Y;
        }

        /// <summary>
        /// Enemy ship at x 100 (structural columns 0-2, operational 3-5, rows 0-36) with a duplicated operational rectangle
        /// (equal-distance ties); a warm shield in front of rows 4-8; a cold shield in front of rows 26-30; crew at row 14; a chain
        /// segment at row 24; a shared attenuating bullet at row 10 (two attenuating beams cross it) and a private one at row 34.
        /// </summary>
        private void BuildTargets()
        {
            Body body = World.CreateBody();
            body.UseWholeBodyBroadPhase = false;
            Ship target = NewShip(1, new V(100f, 0f), 22, "T", body);
            for (int y = 0; y < 37; y++)
                for (int x = 0; x < 6; x++)
                    AddPart(target, new IntVector2(x, y), x < 3 ? HealthType.Structural : HealthType.Operational);
            target.Parts._orderedParts.Add(target.Parts._partsByCell[new IntVector2(0, 0)]);
            void Rect(int left, int width, Category category)
            {
                Fixture fixture = body.CreateRectangle(width, 37f, 1f, new System.Numerics.Vector2(left + width / 2f, 18.5f));
                fixture.CollisionCategories = category;
                fixture.UserData = new ShipPhysics.CellsFixtureInfo(new IntRect(left, 0, width, 37), 1f, fixture);
            }
            Rect(0, 3, Category.Cat3 | Category.Cat4);
            Rect(3, 3, Category.Cat2 | Category.Cat4);
            Rect(3, 3, Category.Cat2 | Category.Cat4); // exact duplicate: equal-fraction ties
            AddShield(target, body, new IntVector2(-8, 6), warm: true);
            ColdShield = AddShield(target, body, new IntVector2(-8, 28), warm: false);
            Fixture chain = body.CreateChainShape(new Vertices { new(-30f, 23.8f), new(-30f, 24.3f), new(-29.5f, 24.3f) });
            chain.CollisionCategories = Category.Cat2;
            AddCrew(new V(60f, 14.5f), 0.6f, target);
            DoomedPart = target.Parts._partsByCell[new IntVector2(0, 21)]; // B11 (spawn y 22) hits row 21
            SharedBullet = AddBullet(new V(40f, 11.5f), 1.4f, pool: 30, "shared"); // crossed by B5 (kills it) and B6 (discarded)
            AddBullet(new V(40f, 34.5f), 0.8f, pool: 500, "private");
        }

        internal void OnDeath(BulletVolumeHit attenuator)
        {
            // Immediate death effect: it destroys a part that a later beam's frozen query hit.
            if (ReferenceEquals(attenuator.Bullet, SharedBullet)) Set(DoomedPart, "p_ship", null);
        }

        private Bullet AddBullet(V location, float radius, int pool, string name)
        {
            var bullet = Shell<Bullet>(); Set(bullet, "_root", Sim);
            var volume = Shell<BulletVolumeHit>(); Set(volume, "<Bullet>k__BackingField", bullet);
            var data = new BulletData { BeamAttenuator = volume };
            Body body = World.CreateBody();
            body.SetTransform(location, 0f);
            body.UserData = data;
            Fixture fixture = body.CreateCircle(radius, 1f);
            fixture.CollisionCategories = Category.Cat12;
            Pools[volume] = pool;
            _names[bullet] = name; _names[volume] = name + "-att";
            return bullet;
        }

        private void AddCrew(V location, float radius, Ship home)
        {
            SpaceCrew crew = Shell<SpaceCrew>();
            Set(crew, "_root", Sim);
            var soul = Shell<CrewSoul>(); Set(soul, "<HomeShip>k__BackingField", home); Set(soul, "<PlayerIndex>k__BackingField", 1);
            Set(crew, "<Soul>k__BackingField", soul);
            Body body = World.CreateBody();
            body.SetTransform(location, 0f);
            body.UserData = crew;
            Fixture fixture = body.CreateCircle(radius, 1f);
            fixture.UserData = crew;
            fixture.CollisionCategories = Category.Cat10;
            _names[crew] = "crew";
        }

        private ArcShield AddShield(Ship target, Body body, IntVector2 location, bool warm)
        {
            var rules = Shell<PartRules>(); rules.Size = new IntVector2(1, 1); Set(rules, "HealthType", HealthType.Operational); Set(rules, "TypeCategories", new HashSet<ID<PartCategory>>());
            var part = Shell<Part>(); Set(part, "<Rules>k__BackingField", rules); Set(part, "p_ship", target); Set(part, "<Location>k__BackingField", location);
            Set(part, "_components", new List<PartComponent>());
            var shieldRules = Shell<ArcShieldRules>();
            var arc = Shell<ModifiableAngle>(); arc.BaseValue = 6.2f; Set(shieldRules, "Arc", arc);
            var radius = Shell<ModifiableFloat>(); radius.BaseValue = 3f; Set(shieldRules, "Radius", radius);
            var shield = Shell<ArcShield>(); Set(shield, "<Part>k__BackingField", part); Set(shield, "<IsOperational>k__BackingField", true);
            Set(shield, "<Rules>k__BackingField", shieldRules);
            Set(shield, "<RulesLocation>k__BackingField", new V(0.5f, 0.5f)); Set(shield, "<RulesRotation>k__BackingField", (Direction)MathF.PI);
            Set(shield, "_hasRulesLoc", true); Set(shield, "_hasRulesRot", true);
            if (warm) { Set(shield, "_cachedArcAngle", (Angle?)(Angle)6.2f); Set(shield, "_cachedArcRadius", (float?)3f); }
            V center = part.TransformPointFromRules(new V(0.5f, 0.5f));
            Fixture fixture = body.CreateCircle(3f, 1f, center);
            fixture.CollisionCategories = Category.Cat9;
            fixture.UserData = shield;
            _names[shield] = warm ? "shield" : "cold-shield";
            _names[part] = _names[shield] + "-part";
            return shield;
        }

        public void Dispose()
        {
            Hooks.Active = null;
            _harmony.UnpatchAll(_harmony.Id);
            BeamFrozen.OverrideForTests(null, BeamFrozen.QueryMode.Workers, allowTestOwner: false);
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

internal static class FrozenTestExtensions
{
    internal static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
