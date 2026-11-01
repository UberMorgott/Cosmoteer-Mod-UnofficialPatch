using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Cosmoteer.Bullets;
using Cosmoteer.Game;
using Cosmoteer.Modes.Creative;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Defenses;
using Cosmoteer.Ships.Parts.Weapons;
using Cosmoteer.Simulation;
using Halfling;
using Halfling.Application;
using Halfling.Geometry;
using Halfling.Performance;
using Halfling.Physics2D.Collision;
using Halfling.Physics2D.Dynamics;
using Halfling.Pooling;
using Halfling.Scene2D;
using Halfling.Timing;
using HarmonyLib;
using V = Halfling.Geometry.Vector2;
using HitInfo = Cosmoteer.Ships.Parts.Weapons.BeamEmitter.HitInfo;
using HitObject = Cosmoteer.Ships.Parts.Weapons.BeamEmitter.HitObject;
using StructuralHitInfo = Cosmoteer.Ships.Parts.Weapons.BeamEmitter.StructuralHitInfo;
using Record = Performance.BeamParallel.Record;

namespace Performance;

/// <summary>
/// Opt-in (<c>PERFORMANCE_BEAM_FROZEN=1</c>): bucket -4 (beam emission) with CHANGED, deterministic semantics, single player
/// only (multiplayer sims stay native until party-wide agreement on the setting exists). The result is deterministic, not
/// equal to vanilla (spec: beam-design.md §9).
/// <para/>
/// The bucket runs in three phases instead of the native per-beam loop (SceneRoot.FixedUpdateForBucket is replaced for -4):
/// P, a serial preparation at bucket start in native order (membership, gate, scales, spawn transform, both RNG draws,
/// range/end point, every query input materialised into a record); Q, the hit queries as a pure function of the world
/// frozen at bucket start (workers run the audited <see cref="BeamParallel.Ctx"/> query; anything unaudited runs the same
/// query serially after the join, guards off); A, a serial apply in native slot order (value listeners, StopEmission,
/// recorded attenuations, the BeamEmitter.cs:857-950 state update and queued hit effects, other bucket members).
/// Worker count, partitioning and timing only decide which thread computes a record, never what it contains.
/// </summary>
internal static class BeamFrozen
{
    internal static readonly bool Requested = BeamParallel.IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_BEAM_FROZEN"));
    /// <summary>Diagnostic (<c>PERFORMANCE_BEAM_FROZEN_ORACLE=1</c>): every query on the main thread through the serial path; results must equal the worker runs.</summary>
    internal static readonly bool OracleRequested = Requested && BeamParallel.IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_BEAM_FROZEN_ORACLE"));

    private static int _ready;
    private static bool _attempted;
    private static string? _failureReason;
    private static Director? _director;
    internal static bool Ready => Volatile.Read(ref _ready) != 0;
    internal static string? FailureReason => Volatile.Read(ref _failureReason);

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? [(BeamParallel.BucketTarget, typeof(BucketHook))] : [];

    internal static bool Initialize()
    {
        if (!Requested) return true;
        if (_attempted) return Ready;
        _attempted = true;
        try
        {
            if (BeamParallel.Requested) throw new NotSupportedException("PERFORMANCE_BEAM_FROZEN and PERFORMANCE_BEAM_PARALLEL are mutually exclusive");
            Director director = App.Director ?? throw new NotSupportedException("Beam frozen requires the native Director");
            BeamParallel.ValidateNative(); // file SHA, MVIDs and IL of every mirrored native method (DoEmitBeam, GetHitInfo, bucket loop, ...)
            Patches? info = Harmony.GetPatchInfo(BeamParallel.BucketTarget);
            if (info == null || info.Prefixes.Count(p => p.owner == Patcher.HarmonyId && p.PatchMethod.DeclaringType == typeof(BucketHook)) != 1)
                throw new NotSupportedException("Beam frozen bucket hook missing");
            WarmUp();
            _director = director;
            director.FrameStarting += OnFrameStarting;
            Volatile.Write(ref _ready, 1);
            Log.Info("Beam frozen ready: bucket -4 runs prepare / frozen " + (_mode == QueryMode.Oracle ? "serial oracle" : "parallel") + " query / serial apply (changed semantics, single player only)");
            return true;
        }
        catch (Exception ex) { Reject(ex.Message); return false; }
    }

    /// <summary>Type initializers and pools of the query path run once on the main thread, never lazily on a worker.</summary>
    internal static void WarmUp()
    {
        TempList<StructuralHitInfo>.Alloc().Dispose();
        TempList<IntVector2>.Alloc().Dispose();
        RuntimeHelpers.RunClassConstructor(typeof(BeamParallel.Ctx).TypeHandle);
    }

    private static void OnFrameStarting(object? sender, EventArgs e) => Stats.MaybeLog();

    internal static void Disable()
    {
        Volatile.Write(ref _ready, 0);
        if (_director == null) return;
        _director.FrameStarting -= OnFrameStarting;
        _director = null;
        Stats.Log("disabled");
    }
    private static void Reject(string reason)
    {
        if (Interlocked.CompareExchange(ref _failureReason, reason, null) == null) Log.Info("Beam frozen disabled: " + reason);
        Disable();
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // Test seams

    internal enum QueryMode : byte { Workers, Oracle }
    /// <summary>Runs the worker round (count, run(i)) instead of FastParallel; null = FastParallel.</summary>
    private static Action<int, Action<int>>? _dispatchOverride;
    private static QueryMode _mode = OracleRequested ? QueryMode.Oracle : QueryMode.Workers;
    private static bool _allowTestOwner;
    /// <summary>Test seam: sees every record after the join, before the apply phase.</summary>
    internal static Action<Session>? AfterQueryForTests;
    internal static void OverrideForTests(Action<int, Action<int>>? dispatch, QueryMode mode, bool allowTestOwner)
    {
        _dispatchOverride = dispatch; _mode = mode; _allowTestOwner = allowTestOwner;
        Bindings.Clear();
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // Per-sim binding: decided at the sim's first bucket -4, never changed (a frame-timed switch would desync a party).

    internal sealed class Binding
    {
        internal bool On;
        /// <summary>Workers may run audited queries (pure function of the mode type); otherwise every query runs serially.</summary>
        internal bool Workers;
        internal string? Reason;
    }
    private static readonly ConditionalWeakTable<SimRoot, Binding> Bindings = new();

    internal static Binding Bind(SimRoot sim)
    {
        if (Bindings.TryGetValue(sim, out Binding? b)) return b;
        b = new Binding();
        if (sim.GetType() != typeof(SimRoot)) b.Reason = "sim type";
        // Single player only: in a party the env flag and the patch check are per client, so peers could disagree.
        else if (!BeamParallel.SinglePlayer(sim)) b.Reason = "multiplayer";
        else if (sim._customFixedUpdateBucketHandlers.ContainsKey(-4)) b.Reason = "custom bucket -4 handler";
        else if (sim.PhysicsWorld?.GetType() != typeof(World) || sim.PhysicsWorld.ContactManager?.GetType() != typeof(ContactManager) ||
            sim.PhysicsWorld.ContactManager.BroadPhase?.GetType() != typeof(DynamicTreeBroadPhase<BodyFixtureProxy>)) b.Reason = "physics world type";
        else if (!_allowTestOwner && !Compatible(out string? error)) b.Reason = error ?? "compatibility";
        b.On = b.Reason == null;
        b.Workers = b.On && sim.Mode?.GetType() == typeof(CreativeSimModeManager) && sim.Game?.GetType() == typeof(GameRoot);
        Bindings.AddOrUpdate(sim, b);
        Log.Info("Beam frozen sim bound: " + (b.On ? "on, " + (b.Workers ? "parallel queries" : "serial queries (mode not audited for workers)") : "native (" + b.Reason + ")"));
        return b;
    }

    /// <summary>
    /// Foreign Harmony patches on the game or Halfling modules make the sim unbound (native), as for BeamParallel. The only
    /// exemption is the bench's phase timing (<see cref="BenchPhaseTiming"/>): a prefix + finalizer pair on FastParallel.For and
    /// its wait loop that only reads timestamps.
    /// </summary>
    internal static bool Compatible(out string? error)
    {
        try
        {
            var view = HarmonyPatchIdentity.ReadRuntimeView();
            var loader = KnownLoaderPatches.Validate(view.Registrations, view.LoaderModules);
            error = FirstForeign(view.Registrations, loader.Allows);
            return error == null;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
    /// <summary>The first registration on the game or Halfling modules that is neither ours, a validated loader patch nor the bench timing.</summary>
    internal static string? FirstForeign(IEnumerable<KnownLoaderPatches.Registration> registrations, Func<KnownLoaderPatches.Registration, bool> loaderAllows)
    {
        foreach (var registration in registrations)
        {
            MethodBase target = registration.Target;
            if (target.Module != typeof(BeamEmitter).Module && target.Module != typeof(World).Module) continue;
            if (registration.Owner == Patcher.HarmonyId || loaderAllows(registration) || BenchPhaseTiming(registration)) continue;
            return "Foreign patch on a beam frozen dependency module: " + KnownLoaderPatches.Describe(registration);
        }
        return null;
    }
    /// <summary>Harmony id of E:\DEV\cosmoteer\Bench src/FramePhaseTiming.cs (BENCH_PHASE_TIMING=1).</summary>
    internal const string BenchPhaseTimingOwner = "morgott.bench.phase-timing";
    private const string BenchPhaseTimingType = "Bench.FramePhaseTiming";
    private static readonly MethodInfo FastParallelFor = AccessTools.Method(typeof(FastParallel), nameof(FastParallel.For));

    /// <summary>Exactly the bench phase-timing registrations: owner, target (FastParallel.For or its _WaitUntilFinished local
    /// function), hook kind and hook method (Bench.FramePhaseTiming.ForPrefix/ForFinalizer/WaitPrefix/WaitFinalizer).</summary>
    internal static bool BenchPhaseTiming(KnownLoaderPatches.Registration r)
    {
        if (r.Owner != BenchPhaseTimingOwner || r.InnerMethod != null || r.Hook.DeclaringType?.FullName != BenchPhaseTimingType) return false;
        string prefix;
        if (SameMethod(r.Target, FastParallelFor)) prefix = "For";
        else if (r.Target.DeclaringType == typeof(FastParallel) && r.Target.Name.Contains("g___WaitUntilFinished|", StringComparison.Ordinal)) prefix = "Wait";
        else return false;
        return r.HookKind switch
        {
            KnownLoaderPatches.Kind.Prefix => r.Hook.Name == prefix + "Prefix",
            KnownLoaderPatches.Kind.Finalizer => r.Hook.Name == prefix + "Finalizer",
            _ => false,
        };
    }
    private static bool SameMethod(MethodBase a, MethodBase b) => a.Module == b.Module && a.MetadataToken == b.MetadataToken;

    // ---------------------------------------------------------------------------------------------------------------------
    // Delivery: SceneRoot.FixedUpdateForBucket(int) (SceneRoot.cs:995-1014), replaced for bucket -4 of a bound sim.

    internal static class BucketHook
    {
        public static bool Prefix(SceneRoot __instance, int bucket)
        {
            if (bucket != -4 || !Ready || __instance is not SimRoot sim || _running) return true;
            if (!ReferenceEquals(sim.SceneThread, Thread.CurrentThread) || sim.IsDoingParallelUpdate) return true;
            if (!_allowTestOwner && !ReferenceEquals(_director?.MainThread, Thread.CurrentThread)) return true;
            Binding binding = Bind(sim);
            if (!binding.On) return true;
            List<IFixedUpdateableSceneObject> list = sim.FixedUpdateables.GetBucket(bucket); // SceneRoot.cs:997-1001
            int count = list.Count;
            if (_members.Length < count) _members = new IFixedUpdateableSceneObject[Math.Max(count, _members.Length * 2)];
            list.CopyTo(_members);
            sim.FixedUpdater.CurrentBucket = bucket;
            RunBucket(sim, binding, _members, count);
            Array.Clear(_members, 0, count); // SceneRoot.cs:1013
            return false;
        }
    }
    private static IFixedUpdateableSceneObject[] _members = new IFixedUpdateableSceneObject[64];
    private static bool _running;

    // ---------------------------------------------------------------------------------------------------------------------
    // Records and session

    internal enum Kind : byte { ListenOnly, Stop, Emit }

    internal sealed class FrozenRecord : Record
    {
        internal Kind Kind;
        internal bool SerialOnly;
        internal bool WorkerRan;
        internal bool UpdateScales;
        internal float ValueScale;
        internal float InitialHitLength;
        internal int AttenuationLimit;
    }

    /// <summary>One bucket slot: a native member, or an EmitBeams group (records [First, First+Count)).</summary>
    internal struct Slot
    {
        internal IFixedUpdateableSceneObject? Native;
        internal int First, Count;
    }

    internal sealed class Session
    {
        internal readonly List<Slot> Slots = [];
        internal readonly List<FrozenRecord> Records = [];
        internal readonly List<FrozenRecord> Work = [];
        internal SimRoot Sim = null!;
        internal Binding Binding = null!;
        internal void Clear() { Slots.Clear(); Records.Clear(); Work.Clear(); Sim = null!; Binding = null!; }
    }
    private static readonly Session Pooled = new();
    private static readonly Stack<FrozenRecord> RecordPool = new();

    /// <summary>The three phases over one bucket snapshot. Exceptions propagate like the native loop's.</summary>
    internal static void RunBucket(SimRoot sim, Binding binding, IFixedUpdateableSceneObject[] members, int count)
    {
        Session s = Pooled;
        s.Clear();
        s.Sim = sim; s.Binding = binding;
        _running = true;
        try
        {
            long t0 = Stopwatch.GetTimestamp();
            Prepare(s, members, count);
            long t1 = Stopwatch.GetTimestamp();
            Query(s);
            AfterQueryForTests?.Invoke(s);
            long t2 = Stopwatch.GetTimestamp();
            Apply(s);
            Stats.Bucket(s, t1 - t0, t2 - t1, Stopwatch.GetTimestamp() - t2);
        }
        finally { End(s); _running = false; }
    }

    /// <summary>Prepare + query only (tests and the oracle comparison); the caller applies or ends the session.</summary>
    internal static Session PrepareAndQuery(SimRoot sim, Binding binding, IFixedUpdateableSceneObject[] members, int count)
    {
        Session s = new() { Sim = sim, Binding = binding };
        Prepare(s, members, count);
        Query(s);
        return s;
    }

    internal static void End(Session s)
    {
        foreach (FrozenRecord r in s.Records)
        {
            r.Info.Dispose(); // results not consumed by the apply phase (cancelled, or an exception)
            r.Info = default;
            r.Emitter = null!; r.Rules = null!; r.Part = null!; r.Ship = null!; r.Sim = null!; r.World = null!; r.ShipTarget = null; r.ActionSource = null; r.Attenuator = null;
            RecordPool.Push(r);
        }
        s.Clear();
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // P: serial preparation (bucket start, main thread, native order)

    private static readonly List<BeamEmitter> _beams = [];

    internal static void Prepare(Session s, IFixedUpdateableSceneObject[] members, int count)
    {
        for (int i = 0; i < count; i++)
        {
            IFixedUpdateableSceneObject member = members[i];
            if (member is CustomFixedUpdateProxy proxy && proxy._callback is { HasSingleTarget: true, Target: WeaponManager manager } callback &&
                BeamParallel.SameMethod(callback.Method, BeamParallel.EmitBeamsMethod))
            {
                var slot = new Slot { First = s.Records.Count };
                if (!manager.SuppressAllWeapons && manager._emittingBeams.Count > 0) // WeaponManager.cs:665-670, snapshot at bucket start
                {
                    _beams.Clear();
                    _beams.AddRange(manager._emittingBeams);
                    foreach (BeamEmitter beam in _beams) PrepareBeam(s, beam);
                    _beams.Clear();
                }
                slot.Count = s.Records.Count - slot.First;
                s.Slots.Add(slot);
            }
            else s.Slots.Add(new Slot { Native = member });
        }
    }

    internal static void PrepareBeam(Session s, BeamEmitter beam)
    {
        FrozenRecord r = RecordPool.Count > 0 ? RecordPool.Pop() : new FrozenRecord();
        r.Emitter = beam; r.Rules = beam.Rules; r.Part = beam.Part!; r.Ship = beam.Ship!; r.Sim = s.Sim; r.World = s.Sim.PhysicsWorld;
        r.Order = s.Records.Count; r.Info = default; r.Outcome = BeamParallel.Outcome.Pending; r.Reason = null; r.QueryTicks = 0; r.Taken = false;
        r.Eligible = false; r.Attenuator = null; r.SerialOnly = false; r.WorkerRan = false; r.Kind = Kind.ListenOnly;
        s.Records.Add(r);
        if (!beam.IsToggleOn) return; // BE:833
        BeamEmitterRules rules = beam.Rules;
        // BE:837-838
        Time time = (rules.InitHitLengthResetDelay > Time.Zero && beam.HasPermissionToFire() && (!beam.IsUnderDirectControl() || beam.DirectControlWantsToFire()) &&
            beam.GetFireTarget(checkProhibited: false, checkArc: true) != null && beam.HasResources(useAnticipated: false))
            ? Mathx.Max(beam._emissionTimeRemaining!.Value, rules.InitHitLengthResetDelay) : beam._emissionTimeRemaining!.Value;
        if (time <= 0f) { r.Kind = Kind.Stop; return; }
        r.Kind = Kind.Emit;
        // BE:843-847: the new scales are in force for every getter below; the fields are restored and written again in A
        // (the BE:832 listener runs first there and sees the old values, as natively).
        float oldEffects = beam._effectsScale, oldValue = beam._valueScale;
        r.UpdateScales = !rules.UpdateScaleFactorsPerEmit;
        if (r.UpdateScales)
        {
            beam._effectsScale = beam._effectsScaleFactor?.Value ?? 1f;
            beam._valueScale = beam._valueScaleFactor?.Value ?? 1f;
        }
        r.EffectsScale = beam._effectsScale; r.ValueScale = beam._valueScale;
        try
        {
            // BE:848-853 (Ship.Rand drawn here, in native order)
            V point = beam.GetWorldSpawnLoc();
            Angle min = rules.Spread.Min.GetValue(beam.Part, beam._effectsScale);
            Angle max = rules.Spread.Max.GetValue(beam.Part, beam._effectsScale);
            Angle angle = beam.Rand.Angle(min, max);
            Direction direction = beam.GetWorldSpawnDir() + angle;
            r.InitialHitLength = beam.InitialHitLength;
            r.Spawn = point; r.Dir = direction; r.IsInit = r.InitialHitLength <= 0f;
            r.LengthField = beam._length; r.InitHitObjectId = beam._initHitObjectID;
            // BE:1055-1079 (the offset point drawn here)
            _ = BeamParallel.Lines1055To1079(beam, point, direction, beam._effectsScale, out r.CurrentRange, out r.InfoLength, out r.SpawnDirVector,
                out r.PreOffsetEnd, out r.WorldOrigin, out Circle? offset);
            r.EndPoint = offset is { } circle ? beam.Ship!.Rand.PointInside(circle) : r.PreOffsetEnd;
            bool exactTarget = BeamParallel.ReadQueryValues(beam, beam._effectsScale, out r.RangeValue, out r.SecondaryWidth, out r.ShipTarget, out r.ActionSource,
                out r.SourcePlayer, out r.ShipPlayer, out r.MaxStructuralPenetrations);
            r.AttenuationLimit = rules.AttenuationLimit?.GetValue(beam.Part, beam._effectsScale) ?? 0;
            // Worker eligibility: a pure function of the frozen state; it decides who computes the query, not its result.
            r.SerialOnly = !s.Binding.Workers || !exactTarget || rules.GetType() != typeof(BeamEmitterRules) || !BeamParallel.StaticEligible(beam, out _);
            r.Eligible = !r.SerialOnly;
        }
        finally
        {
            beam._effectsScale = oldEffects; beam._valueScale = oldValue;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // Q: queries against the frozen world. No mutation between the end of P and the join.

    internal static void Query(Session s)
    {
        if (_mode == QueryMode.Workers && (_dispatchOverride != null || (FastParallel.IsRunning && FastParallel.ThreadCount > 0)))
        {
            foreach (FrozenRecord r in s.Records) if (r.Kind == Kind.Emit && !r.SerialOnly) s.Work.Add(r);
            // Heaviest first (secondary rays); the order only affects load balance.
            s.Work.Sort((a, b) => a.SecondaryWidth.HasValue != b.SecondaryWidth.HasValue ? (a.SecondaryWidth.HasValue ? -1 : 1) : a.Order.CompareTo(b.Order));
            if (s.Work.Count > 0)
            {
                if (_dispatchOverride is { } dispatch) dispatch(s.Work.Count, i => RunWorker(s.Work[i]));
                else FastParallel.For(0, s.Work.Count, ParallelBody, s, batchSize: 1);
            }
        }
        // Join done. Serial path in record order: SerialOnly records and every worker stop (unknown shape, cold cache, ...).
        foreach (FrozenRecord r in s.Records)
        {
            if (r.Kind != Kind.Emit || (r.WorkerRan && r.Outcome == BeamParallel.Outcome.Ready)) continue;
            if (r.WorkerRan) Stats.WorkerStop(r.Reason);
            BeamParallel.RunSerial(r);
            Stats.Serial++;
        }
    }

    private static void RunWorker(FrozenRecord r)
    {
        BeamParallel.Run(r, frozen: true);
        r.WorkerRan = true;
    }
    private static readonly FastParallelAction ParallelBody = (from, to, data) =>
    {
        var s = (Session)data!;
        for (int i = from; i < to; i++) RunWorker(s.Work[i]);
    };

    // ---------------------------------------------------------------------------------------------------------------------
    // A: serial apply, native slot order

    internal static void Apply(Session s)
    {
        foreach (Slot slot in s.Slots)
        {
            if (slot.Native != null) { slot.Native.FixedUpdate(s.Sim.FixedUpdater, s.Sim); continue; } // SceneRoot.cs:1010
            for (int i = slot.First; i < slot.First + slot.Count; i++) ApplyRecord(s, s.Records[i]);
        }
    }

    internal static void ApplyRecord(Session s, FrozenRecord r)
    {
        BeamEmitter beam = r.Emitter;
        BeamParallel.BeamValueChanged(beam)?.Invoke(beam, EventArgs.Empty); // A1: BE:832
        if (r.Kind == Kind.ListenOnly) return;
        // A2: an earlier application stopped or removed this emitter -> cancelled (bucket-start result otherwise kept).
        if (!beam.IsToggleOn || beam._emissionTimeRemaining == null || !ReferenceEquals(beam.Part, r.Part) || !ReferenceEquals(beam.Part?.Ship, r.Ship) || !ReferenceEquals(beam.Sim, s.Sim))
        {
            Stats.Cancelled++;
            return;
        }
        if (r.Kind == Kind.Stop) { beam.StopEmission(); return; } // A3: BE:840
        // A4: BE:843-847, BE:1080
        if (r.UpdateScales) { beam._effectsScale = r.EffectsScale; beam._valueScale = r.ValueScale; }
        beam._tempWorldSpawnLoc = r.Spawn;
        // A5: BE:1235 (serial: shared damage pools are consumed in apply order; a death runs its immediate effects here)
        if (r.Attenuator is { } attenuator)
        {
            if (attenuator is BulletComponent { Bullet: { IsMarkedForDestruction: false } } || attenuator is not BulletComponent)
                attenuator.AttenuateBeam(r.AttenuationSegment, r.AttenuationLimit);
            else Stats.DiscardedAttenuations++;
        }
        HitInfo hitInfo = r.Info;
        r.Info = default; // ownership passes here
        try
        {
            DropDestroyed(ref hitInfo, s.Sim);
            ApplyHit(beam, r, hitInfo);
        }
        finally { hitInfo.Dispose(); } // BE:954
    }

    /// <summary>A6: hit objects removed from the sim by an earlier application are dropped (defined discarded hit, no re-query).</summary>
    internal static void DropDestroyed(ref HitInfo info, SimRoot sim)
    {
        HitObject hit = info.Operational.Hit;
        if ((hit.Part != null && !ReferenceEquals(hit.Part.Sim, sim)) || (hit.Shield != null && !ReferenceEquals(hit.Shield.Sim, sim)) ||
            (hit.Crew != null && !ReferenceEquals(hit.Crew.Sim, sim)))
        {
            info.Operational.Hit = default;
            Stats.DroppedTargets++;
        }
        if (info.Structural.Part != null && !ReferenceEquals(info.Structural.Part.Sim, sim)) { info.Structural = default; Stats.DroppedTargets++; }
        if (info.AllStructuralHits is { } all)
            for (int i = 0; i < all.Count; i++)
                if (all[i].Part is { } part && !ReferenceEquals(part.Sim, sim)) { all[i] = default; Stats.DroppedTargets++; }
    }

    /// <summary>A7: BeamEmitter.cs:857-950 with the prepared spawn point, direction and initial hit length.</summary>
    internal static void ApplyHit(BeamEmitter beam, FrozenRecord r, HitInfo hitInfo)
    {
        BeamEmitterRules rules = beam.Rules;
        V point = r.Spawn;
        Direction direction = r.Dir;
        float initialHitLength = r.InitialHitLength;
        beam._length = hitInfo.Length;
        beam._effectSeg = new Segment(in point, in hitInfo.EndPoint);
        if (hitInfo.Operational.Hit.HasObject() || hitInfo.Structural.Part != null)
        {
            ObjectID objectID = (hitInfo.Operational.Hit.Part?.Ship ?? hitInfo.Structural.Part?.Ship ?? hitInfo.Operational.Hit.Shield?.Ship)?.UniqueID ??
                hitInfo.Operational.Hit.Crew?.UniqueID ?? hitInfo.Operational.Hit.Attenuator!.UniqueID;
            if (objectID != beam._initHitObjectID || initialHitLength <= 0f)
            {
                beam._initialHitLength = beam._length;
                beam._initHitObjectID = objectID;
            }
        }
        if (beam._beamEffect != null)
        {
            beam._beamEffect.Parameters.CustomShape = beam;
            beam._beamEffect.Parameters.BeamLength = beam._length + rules.BeamMediaEffectsLengthOffset;
            beam._beamEffect.Parameters.Intensity = rules.BeamMediaEffectsIntensity.GetValue(beam.Part, beam._effectsScale) * Mathx.Lerp(rules.MediaEffectsScaleOverRange, beam.GetRangeT()) *
                Mathx.Lerp(rules.MediaEffectsScaleOverRampUp, beam.GetRampUpT());
            beam._beamEffect.Parameters.FrameOfReference = beam.Ship!.Physics.Body!.GetLinearVelocityFromWorldPoint(point);
            beam._beamEffect.Parameters.WorldVelocity = beam._beamEffect.Parameters.FrameOfReference;
            beam._beamEffect.Parameters.SourceShip = beam.Ship;
            beam._beamEffect.Parameters.TargetShip = beam.GetShipTarget(checkProhibited: true);
            Part? part = hitInfo.Operational.Hit.Shield?.Part ?? hitInfo.Operational.Hit.Part ?? hitInfo.Structural.Part;
            beam._beamEffect.Parameters.HitPart = part;
            beam._beamEffect.Parameters.HitShip = part?.Ship;
            beam._beamEffect.Parameters.HitShield = hitInfo.Operational.Hit.Shield;
            beam._beamEffect.Parameters.HitCrew = hitInfo.Operational.Hit.Crew;
        }
        beam.UpdateMediaNodes(hitInfo.Operational.Hit.Part, hitInfo.Structural.Part, hitInfo.Operational.Hit.Shield, hitInfo.Operational.Hit.Crew, hitInfo.Operational.Hit.Attenuator,
            point, direction, hitInfo.EndPoint, hitInfo.Operational.WorldPoint, hitInfo.Operational.WorldNormal, hitInfo.Structural.WorldPoint, hitInfo.Structural.WorldNormal);
        bool flag = false;
        while (beam._damageTimeRemaining <= 0f)
        {
            beam._damageTimeRemaining += rules.HitInterval.GetValue(beam.Part, beam._effectsScale);
            if (hitInfo.Operational.Hit.Part != null)
            {
                beam.DoHitEffect(hitInfo.Operational.Hit.Part.Ship, hitInfo.Operational.Hit.Part, hitInfo.Operational.Hit.Collider, null, null, rules.HitOperational,
                    hitInfo.Operational.WorldPoint, direction, hitInfo.Operational.WorldNormal);
                flag = true;
            }
            if (hitInfo.AllStructuralHits != null)
            {
                for (int i = 0; i < hitInfo.AllStructuralHits.Count; i++)
                {
                    StructuralHitInfo structuralHitInfo = hitInfo.AllStructuralHits[i];
                    if (structuralHitInfo.Part != null)
                    {
                        beam.DoHitEffect(structuralHitInfo.Part.Ship, structuralHitInfo.Part, structuralHitInfo.Collider, null, null, rules.HitStructural,
                            structuralHitInfo.WorldPoint, direction, structuralHitInfo.WorldNormal, i + 1);
                        flag = true;
                    }
                }
            }
            else if (hitInfo.Structural.Part != null)
            {
                beam.DoHitEffect(hitInfo.Structural.Part.Ship, hitInfo.Structural.Part, hitInfo.Structural.Collider, null, null, rules.HitStructural,
                    hitInfo.Structural.WorldPoint, direction, hitInfo.Structural.WorldNormal, 1);
                flag = true;
            }
            if (hitInfo.Operational.Hit.Shield != null)
            {
                beam.DoHitEffect(hitInfo.Operational.Hit.Shield.Ship, hitInfo.Operational.Hit.Shield.Part, null, hitInfo.Operational.Hit.Shield, null, rules.HitShield,
                    hitInfo.Operational.WorldPoint, direction, hitInfo.Operational.WorldNormal);
                flag = true;
            }
            if (hitInfo.Operational.Hit.Crew != null)
            {
                beam.DoHitEffect(null, null, null, null, hitInfo.Operational.Hit.Crew, rules.HitCrew, hitInfo.Operational.WorldPoint, direction, hitInfo.Operational.WorldNormal);
                flag = true;
            }
            if (hitInfo.Operational.Hit.Attenuator != null)
            {
                beam.DoHitEffect(null, null, null, null, null, rules.HitAttenuator, hitInfo.Operational.WorldPoint, direction, hitInfo.Operational.WorldNormal);
                flag = true;
            }
            if (!flag)
            {
                beam.DoHitEffect(null, null, null, null, null, rules.HitNothing, hitInfo.EndPoint, direction, (hitInfo.EndPoint - point).Normalize());
            }
            BeamParallel.BeamTrigger(beam)?.Invoke(beam, beam._effectsScale); // BE:929
        }
        if (flag)
        {
            if (!beam._hitStartTime.HasValue || beam._hitEndTime.HasValue)
            {
                beam._hitStartTime = beam.Sim!.LogicalTime;
                beam._hitEndTime = null;
            }
        }
        else if (beam._hitStartTime.HasValue && !beam._hitEndTime.HasValue)
        {
            beam._hitEndTime = beam.Sim!.LogicalTime;
        }
        BeamParallel.BeamValueChanged(beam)?.Invoke(beam, EventArgs.Empty); // BE:948
        beam._emissionTimeRemaining -= beam.Sim!.FixedUpdater.Interval;
        beam._damageTimeRemaining -= beam.Sim.FixedUpdater.Interval;
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // Instrumentation (main thread only)

    internal static class Stats
    {
        internal static long Ticks, Records, Emits, WorkerReady, Serial, Cancelled, DiscardedAttenuations, DroppedTargets, Attenuations, PrepTicks, QueryTicks, ApplyTicks, WorkerQueryTicks;
        internal static readonly Dictionary<string, long> Stops = new(StringComparer.Ordinal);
        private static long _lastLog = Stopwatch.GetTimestamp();
        internal static void WorkerStop(string? reason)
        {
            reason ??= "?";
            Stops[reason] = Stops.GetValueOrDefault(reason) + 1;
        }
        internal static void Bucket(Session s, long prep, long query, long apply)
        {
            Ticks++; Records += s.Records.Count; PrepTicks += prep; QueryTicks += query; ApplyTicks += apply;
            foreach (FrozenRecord r in s.Records)
            {
                if (r.Kind != Kind.Emit) continue;
                Emits++;
                if (r.WorkerRan && r.Outcome == BeamParallel.Outcome.Ready) { WorkerReady++; WorkerQueryTicks += r.QueryTicks; }
                if (r.Attenuator != null) Attenuations++;
            }
            if (Ticks == 1 || Ticks % 500 == 0) Log(Ticks == 1 ? "first bucket" : "periodic");
        }
        internal static void MaybeLog()
        {
            if (Ticks == 0 || Stopwatch.GetElapsedTime(_lastLog).TotalSeconds < 30) return;
            Log("periodic");
        }
        internal static void Log(string label)
        {
            _lastLog = Stopwatch.GetTimestamp();
            if (Ticks == 0) { Performance.Log.Info("Beam frozen " + label + ": no buckets"); return; }
            double ms = 1000.0 / Stopwatch.Frequency, n = Ticks;
            Performance.Log.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Beam frozen {label}: ticks {Ticks} records/tick {Records / n:F1} emit/tick {Emits / n:F1} worker/tick {WorkerReady / n:F1} serial/tick {Serial / n:F2} cancelled {Cancelled} attenuations {Attenuations} discarded-attenuations {DiscardedAttenuations} dropped-targets {DroppedTargets} | " +
                $"ms/tick prepare {PrepTicks * ms / n:F3} query {QueryTicks * ms / n:F3} (worker query cpu {WorkerQueryTicks * ms / n:F3}) apply {ApplyTicks * ms / n:F3} | worker stops ") +
                string.Join("; ", Stops.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + "=" + kv.Value)));
        }
    }
}
