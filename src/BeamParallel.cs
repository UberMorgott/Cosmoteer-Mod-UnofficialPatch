using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using Cosmoteer;
using Cosmoteer.Bullets;
using Cosmoteer.Bullets.Death;
using Cosmoteer.Bullets.Hits;
using Cosmoteer.Crew;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Modes;
using Cosmoteer.Modes.Creative;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Colliders;
using Cosmoteer.Ships.Parts.Defenses;
using Cosmoteer.Ships.Parts.Effects;
using Cosmoteer.Ships.Parts.Logic;
using Cosmoteer.Ships.Parts.Weapons;
using Cosmoteer.Simulation;
using Cosmoteer.Simulation.HitEffects;
using Halfling;
using Halfling.Application;
using Halfling.Geometry;
using Halfling.Performance;
using Halfling.Physics2D.Collision;
using Halfling.Physics2D.Collision.Shapes;
using Halfling.Physics2D.Common;
using Halfling.Physics2D.Common.Maths;
using Halfling.Physics2D.Dynamics;
using Halfling.Pooling;
using Halfling.Random;
using Halfling.Scene2D;
using HarmonyLib;
using V = Halfling.Geometry.Vector2;
using NV = System.Numerics.Vector2;
using HitInfo = Cosmoteer.Ships.Parts.Weapons.BeamEmitter.HitInfo;
using HitObject = Cosmoteer.Ships.Parts.Weapons.BeamEmitter.HitObject;
using StructuralHitInfo = Cosmoteer.Ships.Parts.Weapons.BeamEmitter.StructuralHitInfo;

namespace Performance;

/// <summary>
/// Opt-in prototype (<c>PERFORMANCE_BEAM_PARALLEL=1</c>, single-player only): the beam hit queries of sim bucket -4 run in
/// parallel before the bucket, and the native serial loop then uses each result only when every query input matches.
/// <para/>
/// Bucket -4 holds only the per-ship <c>WeaponManager.EmitBeams</c> proxies. At its start (<see cref="BucketHook"/>) the
/// emitting beams are predicted in native order on the main thread: the spread and offset draws run on a copy of each
/// ship's <see cref="FastRand"/> state, targets and rule values are read natively. One speculative round per tick:
/// <see cref="FastParallel"/> runs a handwritten copy of <c>BeamEmitter.GetHitInfo</c> lines 1082-1137 for every
/// eligible record. Workers read only frozen sim state through audited code (own traversal wrappers that check the
/// fixture shape before native Shape.RayCast, own copies of the hit callbacks using per-record spawn point, target and
/// rule values), write nothing outside their record, and stop at any unaudited object (derived crew type, cold shield,
/// unknown shape or user data, missing player state): that beam then runs natively. Exact SpaceCrew hits are handled
/// (EffectFilter.ValidateHitCrew reads only frozen fields and known player states).
/// <para/>
/// Replay is the unchanged native loop. The GetHitInfo prefix recomputes lines 1055-1079 natively, compares every input
/// bit for bit, consumes the native RNG draw, writes <c>_tempWorldSpawnLoc</c> and returns the record. Anything that can
/// change query-visible state mid-bucket (StopEmission, attenuation, bullet death, an immediate hit effect, an unadmitted
/// value listener or trigger subscriber, any input mismatch) invalidates every remaining record; the rest of the tick is
/// native. A query that would attenuate a bullet is never replayed: that beam runs natively.
/// </summary>
internal static class BeamParallel
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_BEAM_PARALLEL"));
    internal static bool IsRequested(string? value) => value == "1";
    /// <summary>Differential mode: records are validated as usual but never substituted; the native result is compared bit for bit.</summary>
    internal static readonly bool VerifyRequested = Requested && IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_BEAM_PARALLEL_VERIFY"));
    internal static bool Verify = VerifyRequested;

    private static int _ready;
    private static bool _attempted;
    private static string? _failureReason;
    private static Director? _director;
    internal static bool Ready => Volatile.Read(ref _ready) != 0;
    internal static string? FailureReason => Volatile.Read(ref _failureReason);

    private static readonly Guid GameMvid = new("fd7c0bba-64d3-40b4-a571-bf8b34eb4277");
    private static readonly Guid HalflingMvid = new("29d04acb-ae49-4f3b-8499-f03c9144a3a9");
    private const string GameFileSha = "F9451FBED4A11396C816D3F44EBDD836E48BCE68BA6AEAE28C3E534E037207D4";
    private const string HalflingFileSha = "80213C7C8182CE63B9075A48DFD80A7614B1FA2AB3BCD16A69D5363CF9A4B391";
    internal const string NativeIlSha256 = "74B009B55234500C8D578D6CE3B55A289C1FD3E5DF8492387A75308F0DD166A5";

    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    internal static readonly MethodInfo BucketTarget = AccessTools.Method(typeof(SceneRoot), "FixedUpdateForBucket", [typeof(int)]);
    internal static readonly MethodInfo EmitTarget = typeof(BeamEmitter).GetMethods(All).Single(m => m.Name.EndsWith(".DoEmitBeam", StringComparison.Ordinal));
    internal static readonly MethodInfo HitInfoTarget = AccessTools.Method(typeof(BeamEmitter), "GetHitInfo");
    internal static readonly MethodInfo StopTarget = AccessTools.Method(typeof(BeamEmitter), "StopEmission");
    internal static readonly MethodInfo AttenuateTarget = AccessTools.Method(typeof(BulletVolumeHit), "AttenuateBeam");
    internal static readonly MethodInfo DieTarget = AccessTools.Method(typeof(BaseBulletDeath), "Die");
    internal static readonly MethodInfo EffectTarget = AccessTools.Method(typeof(MultiHitEffectRules), "DoEffect", [typeof(HitEffectParams)]);
    internal static readonly MethodInfo EmitBeamsMethod = AccessTools.Method(typeof(WeaponManager), "EmitBeams");
    private static readonly MethodInfo NativeValidateHit = AccessTools.Method(typeof(BeamEmitter), "ValidateHit");
    private static readonly MethodInfo MultiValueListener = AccessTools.Method(typeof(PartMultiValue), "OnValueChanged");
    private static readonly MethodInfo ProxyListener = AccessTools.Method(typeof(BeamEmitterProxy), "OnEmitterValueChanged");
    private static readonly FieldInfo ValueChangedField = AccessTools.Field(typeof(BeamEmitter), "e_valueChanged");
    internal static readonly AccessTools.FieldRef<BeamEmitter, EventHandler<EventArgs>?> BeamValueChanged = AccessTools.FieldRefAccess<BeamEmitter, EventHandler<EventArgs>?>("e_valueChanged");
    internal static readonly AccessTools.FieldRef<BeamEmitter, EventHandler<TriggerEventArgs>?> BeamTrigger = AccessTools.FieldRefAccess<BeamEmitter, EventHandler<TriggerEventArgs>?>("HitIntervalElapsed");
    private static readonly AccessTools.FieldRef<PartMultiValue, EventHandler<EventArgs>?> MultiValueChanged = AccessTools.FieldRefAccess<PartMultiValue, EventHandler<EventArgs>?>("ValueChanged");
    private static readonly AccessTools.FieldRef<BeamEmitterProxy, EventHandler<EventArgs>?> ProxyValueChanged = AccessTools.FieldRefAccess<BeamEmitterProxy, EventHandler<EventArgs>?>("ValueChanged");
    private static readonly ID<PartRules> PrismPart = new("cosmoteer.ion_beam_prism");
    private static readonly ID<PartComponentRules> PrismValue = new("EmitterValue");

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ?
    [
        (BucketTarget, typeof(BucketHook)), (EmitTarget, typeof(EmitHook)), (HitInfoTarget, typeof(TakeHook)),
        (StopTarget, typeof(StopHook)), (AttenuateTarget, typeof(AttenuateHook)), (DieTarget, typeof(DieHook)), (EffectTarget, typeof(EffectHook)),
    ] : [];

    /// <summary>Every native method the handwritten query, the replay recomputation and the hooks mirror.</summary>
    internal static IEnumerable<MethodBase> NativeDependencies()
    {
        MethodInfo M(Type type, string name) => type.GetMethod(name, All) ?? throw new MissingMethodException(type.FullName, name);
        IEnumerable<MethodInfo> Locals(Type type, string owner) => type.GetMethods(All).Where(m => m.Name.StartsWith("<" + owner + ">g__", StringComparison.Ordinal)).OrderBy(m => m.Name, StringComparer.Ordinal);
        Type beam = typeof(BeamEmitter);
        foreach (string name in new[] { "GetHitInfo", "ValidateHit", "IsShipHitValid", "ValidateSecondaryHit", "DoOperationalRaycast", "DoStructuralRaycast", "DoSecondaryRaycast", "StopEmission", "GetCurrentRange", "GetStretchRange", "get_InitialHitLength", "GetRangeT", "GetRampUpT", "Cosmoteer.Ships.Parts.Logic.IComponentValueProvider.get_Value" })
            yield return M(beam, name);
        yield return EmitTarget;
        foreach (string owner in new[] { "GetHitInfo", "IsShipHitValid", "ValidateSecondaryHit" })
            foreach (MethodInfo local in Locals(beam, owner)) yield return local;
        yield return M(typeof(WeaponManager), "EmitBeams");
        yield return M(typeof(Emitter), "GetWorldSpawnLoc");
        yield return M(typeof(Emitter), "GetWorldSpawnDir");
        foreach (MethodInfo m in typeof(PhysicsExtensions).GetMethods(All).Where(m => m.Name == "RayCastFirst" && m.GetParameters()[0].ParameterType == typeof(World)).OrderBy(m => m.MetadataToken))
            yield return m;
        yield return M(typeof(World), "RayCast");
        yield return M(typeof(World), "RayCastCallbackBroadPhaseWrapper");
        yield return M(typeof(World), "RayCastCallbackFixturePhaseWrapper");
        yield return M(typeof(Fixture), "RayCast");
        yield return BucketTarget;
        yield return M(typeof(PartMultiValue), "OnValueChanged");
        yield return M(typeof(PartMultiValue), "OnValuesUpdated");
        yield return M(typeof(PartMultiValue), "set_Value");
        yield return M(typeof(BasePartMultiValue), "CalculateValue");
        yield return M(typeof(BasePartMultiValue), "CombineValues");
        yield return M(typeof(BasePartMultiValue), "RoundValue");
        yield return M(typeof(BeamEmitterProxy), "OnEmitterValueChanged");
        yield return M(typeof(BeamEmitterProxy), "get_Value");
        Type occlusion = typeof(PartTileLineScoreValue.LineOcclusionHandler);
        foreach (string name in new[] { "OnFixedUpdate", "HandleOcclusionForLine", "TryGetOccludingObject", "TryGetOccludingFixture", "DoHitEffects", "CreateOrUpdateMediaEffectNode", "EndMediaEffects", "CreateMediaEffectNode" })
            yield return M(occlusion, name);
        foreach (MethodInfo local in Locals(occlusion, "HandleOcclusionForLine").Concat(Locals(occlusion, "TryGetOccludingFixture"))) yield return local;
        yield return M(typeof(PartsManager.FixedUpdateCallbacks), "FixedUpdate");
        // Crew hits (worker copy of the SpaceCrew branches; CrewStop mirrors the reads of ValidateHitCrew).
        Type filter = typeof(EffectFilter);
        yield return M(filter, "ValidateHitCrew");
        yield return M(filter, "get_AffectsCrew");
        foreach (MethodInfo m in filter.GetMethods(All).Where(m => m.Name == "ValidateAllegiance").OrderBy(m => m.MetadataToken)) yield return m;
        yield return M(typeof(SimModeManager), "AreEnemies");
        yield return M(typeof(SimModeManager), "CanDamage");
        yield return M(typeof(CreativeSimModeManager), "CanDamage");
        yield return M(typeof(Ship), "ExistsInSim");
        yield return M(typeof(CrewSoul), "get_HomeShip");
        yield return M(typeof(CrewSoul), "get_PlayerIndex");
        yield return M(typeof(SpaceCrew), "get_Soul");
        yield return M(typeof(SimNode), "get_Sim");
        yield return M(typeof(SimObject), "get_UniqueID");
    }

    internal static bool Initialize()
    {
        if (!Requested) return true;
        if (_attempted) return Ready;
        _attempted = true;
        try
        {
            if (BeamTargetMemo.Requested) throw new NotSupportedException("Beam parallel and beam target memo are mutually exclusive");
            Director director = App.Director ?? throw new NotSupportedException("Beam parallel requires the native Director");
            if (director.FrameCount != 0) throw new NotSupportedException("Beam parallel requires startup FrameCount == 0");
            ValidateNative();
            ValidateHookCoverage();
            if (!Compatible(out string? error)) throw new NotSupportedException(error);
            // Type initializers and pools of the worker path run once on the main thread, never lazily on a worker.
            TempList<StructuralHitInfo>.Alloc().Dispose();
            TempList<IntVector2>.Alloc().Dispose();
            RuntimeHelpers.RunClassConstructor(typeof(Ctx).TypeHandle);
            _director = director;
            director.FrameStarting += OnFrameStarting;
            Volatile.Write(ref _ready, 1);
            Log.Info("Beam parallel prototype ready: single-player bucket -4 hit queries run speculatively, validated replay");
            return true;
        }
        catch (Exception ex) { Reject(ex.Message); return false; }
    }

    internal static void ValidateNative()
    {
        PinFile(typeof(BeamEmitter).Assembly, GameFileSha);
        PinFile(typeof(World).Assembly, HalflingFileSha);
        foreach (MethodBase method in NativeDependencies())
        {
            Guid expected = method.Module == typeof(World).Module ? HalflingMvid : GameMvid;
            if (method.Module.ModuleVersionId != expected) throw new NotSupportedException("Beam parallel native MVID changed: " + method);
        }
        _ = Patcher.RequireIl(NativeDependencies().SelectMany(m => PatchProcessor.GetOriginalInstructions(m)), NativeIlSha256, "Beam parallel native dependencies");
    }
    private static void PinFile(Assembly assembly, string expected)
    {
        using FileStream input = File.OpenRead(assembly.Location);
        if (Convert.ToHexString(SHA256.HashData(input)) != expected) throw new NotSupportedException("Beam parallel native file changed: " + assembly.GetName().Name);
    }
    private static void ValidateHookCoverage()
    {
        foreach ((MethodBase target, Type patch) in Patches)
        {
            Patches? info = Harmony.GetPatchInfo(target);
            bool Has(IEnumerable<Patch>? list, string name) => patch.GetMethod(name, BindingFlags.Public | BindingFlags.Static) == null ||
                (list ?? []).Count(p => p.owner == Patcher.HarmonyId && p.PatchMethod.DeclaringType == patch && p.PatchMethod.Name == name) == 1;
            if (info == null || !Has(info.Prefixes, "Prefix") || !Has(info.Finalizers, "Finalizer") || !Has(info.Transpilers, "Transpiler"))
                throw new NotSupportedException("Beam parallel hook coverage missing: " + target.DeclaringType?.Name + "." + target.Name);
        }
    }
    internal static bool Compatible(out string? error)
    {
        try
        {
            var view = HarmonyPatchIdentity.ReadRuntimeView();
            var loader = KnownLoaderPatches.Validate(view.Registrations, view.LoaderModules);
            foreach (var registration in view.Registrations)
            {
                Module module = registration.Target.Module;
                if (module != typeof(BeamEmitter).Module && module != typeof(World).Module) continue;
                if (registration.Owner == Patcher.HarmonyId || loader.Allows(registration)) continue;
                error = "Foreign patch on a beam parallel dependency module: " + KnownLoaderPatches.Describe(registration);
                return false;
            }
            error = null;
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
    private static readonly Dictionary<MethodBase, byte[]>? HarmonyState =
        AccessTools.Field(typeof(Harmony).Assembly.GetType("HarmonyLib.HarmonySharedState"), "state")?.GetValue(null) as Dictionary<MethodBase, byte[]>;
    private static MethodBase[] _checkedKeys = [];
    private static byte[][] _checkedValues = [];
    private static int _checkedCount = -1;
    /// <summary>
    /// True only when Harmony's shared state holds exactly the method and patch-info array references seen at the last
    /// successful full check, in the same order. Every patch, unpatch or retarget stores a new serialized array
    /// (HarmonySharedState.UpdatePatchInfo), so any change fails the reference comparison. Compared under Harmony's own lock;
    /// allocation-free. An unreadable state never matches (the full check then runs every frame).
    /// </summary>
    internal static bool HarmonyStateUnchanged()
    {
        Dictionary<MethodBase, byte[]>? state = HarmonyState;
        if (state == null) return false;
        lock (state)
        {
            if (state.Count != _checkedCount) return false;
            int i = 0;
            foreach (KeyValuePair<MethodBase, byte[]> entry in state)
            {
                if (!ReferenceEquals(entry.Key, _checkedKeys[i]) || !ReferenceEquals(entry.Value, _checkedValues[i])) return false;
                i++;
            }
            return true;
        }
    }
    internal static void RememberHarmonyState()
    {
        Dictionary<MethodBase, byte[]>? state = HarmonyState;
        if (state == null) return;
        lock (state)
        {
            if (_checkedKeys.Length < state.Count) { _checkedKeys = new MethodBase[state.Count * 2]; _checkedValues = new byte[state.Count * 2][]; }
            int i = 0;
            foreach (KeyValuePair<MethodBase, byte[]> entry in state) { _checkedKeys[i] = entry.Key; _checkedValues[i] = entry.Value; i++; }
            _checkedCount = state.Count;
        }
    }
    internal static void CheckCompatibility()
    {
        if (!Ready || HarmonyStateUnchanged()) return; // no patch changed since the last full check
        try
        {
            // Remembered before the full check: a patch landing during it fails the next comparison and re-runs the check.
            RememberHarmonyState();
            ValidateHookCoverage();
            if (!Compatible(out string? error)) { _checkedCount = -1; Reject(error ?? "Beam parallel compatibility unavailable"); }
        }
        catch (Exception ex) { _checkedCount = -1; Reject(ex.Message); }
    }
    private static void OnFrameStarting(object? sender, EventArgs e)
    {
        CheckCompatibility();
        Stats.MaybeLog();
    }
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
        if (Interlocked.CompareExchange(ref _failureReason, reason, null) == null) Log.Info("Beam parallel disabled: " + reason);
        Disable();
    }
    /// <summary>Test seam: runs the speculative round without FastParallel (worker count under test control).</summary>
    private static Action<int, Action<int>>? _dispatchOverride;
    /// <summary>Test seam: accept a bound simulation that is not attached to a native Director main thread.</summary>
    private static bool _allowTestOwner;
    internal static void OverrideForTests(Action<int, Action<int>>? dispatch, bool allowTestOwner) { _dispatchOverride = dispatch; _allowTestOwner = allowTestOwner; }

    // ---------------------------------------------------------------------------------------------------------------------
    // Records and session

    internal enum Outcome : byte { Pending, Ready, Native, Attenuates }

    internal class Record
    {
        internal BeamEmitter Emitter = null!;
        internal BeamEmitterRules Rules = null!;
        internal Part Part = null!;
        internal Ship Ship = null!;
        internal SimRoot Sim = null!;
        internal World World = null!;
        internal int Order;
        // Inputs (predicted in native order, compared bit for bit at replay).
        internal V Spawn;
        internal Direction Dir;
        internal bool IsInit;
        internal float LengthField;
        internal float EffectsScale;
        internal ObjectID InitHitObjectId;
        internal float CurrentRange;
        internal float InfoLength;
        internal V SpawnDirVector;
        internal V PreOffsetEnd;
        internal V EndPoint;
        internal V WorldOrigin;
        internal float RangeValue;
        internal float? SecondaryWidth;
        internal Ship? ShipTarget;
        internal ActionSourcePlayer? ActionSource;
        internal int SourcePlayer;
        internal int ShipPlayer;
        internal int MaxStructuralPenetrations;
        internal bool Eligible;
        // Result (written only by the worker that owns the record, read after the join).
        internal HitInfo Info;
        internal Outcome Outcome;
        internal string? Reason;
        internal long QueryTicks;
        internal bool Taken;
        // Frozen mode (BeamFrozen): the BE:1235 attenuation the query met, applied serially in the apply phase.
        internal IBeamAttenuator? Attenuator;
        internal Segment AttenuationSegment;
    }

    internal sealed class Session
    {
        internal readonly List<Record> Records = [];
        internal readonly List<Record> Work = [];
        internal readonly Dictionary<BeamEmitter, Record> ByEmitter = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<Ship, FastRand> Predicted = new(ReferenceEqualityComparer.Instance);
        internal SimRoot Sim = null!;
        internal int Thread;
        internal bool Valid;
        internal string? InvalidReason;
        internal BeamEmitter? Current;
        internal void Clear()
        {
            Records.Clear(); Work.Clear(); ByEmitter.Clear(); Predicted.Clear();
            Sim = null!; Valid = false; InvalidReason = null; Current = null;
        }
    }

    private static Session? _active;
    private static readonly Session Pooled = new();
    private static readonly Stack<Record> RecordPool = new();
    private static readonly Stack<FastRand> RandPool = new();

    internal static Session? Active => _active;

    internal static void Invalidate(string reason)
    {
        Session? s = _active;
        if (s == null || !s.Valid || Environment.CurrentManagedThreadId != s.Thread) return;
        s.Valid = false;
        s.InvalidReason = reason;
        Stats.Fallback("invalidate: ", reason);
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // Hooks

    internal static class BucketHook
    {
        public static void Prefix(SceneRoot __instance, int bucket, out Session? __state)
        {
            __state = null;
            if (bucket != -4 || !Ready || _active != null || __instance is not SimRoot sim) return;
            try { __state = Begin(sim); }
            catch (Exception ex)
            {
                Log.Error("beam-parallel-begin", ex);
                if (_active != null) { End(_active); __state = null; }
            }
        }
        public static Exception? Finalizer(Session? __state, Exception? __exception)
        {
            if (__state != null) End(__state);
            return __exception;
        }
    }

    /// <summary>Inserted before each native <c>e_valueChanged</c> invocation of DoEmitBeam (lines 832 and 948).</summary>
    internal static class EmitHook
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = instructions.ToList();
            MethodInfo hook = AccessTools.Method(typeof(EmitHook), nameof(BeforeValueEvent));
            var loads = code.Select((c, i) => (c, i)).Where(t => t.c.opcode == OpCodes.Ldfld && Equals(t.c.operand, ValueChangedField)).Select(t => t.i).ToList();
            if (loads.Count != 2) throw new NotSupportedException("Beam parallel: DoEmitBeam value event shape changed");
            for (int k = loads.Count - 1; k >= 0; k--)
            {
                int at = loads[k] - 1;
                if (code[at].opcode != OpCodes.Ldarg_0) throw new NotSupportedException("Beam parallel: DoEmitBeam value event receiver changed");
                var first = new CodeInstruction(OpCodes.Ldarg_0);
                first.labels.AddRange(code[at].labels); code[at].labels.Clear();
                first.blocks.AddRange(code[at].blocks); code[at].blocks.Clear();
                code.InsertRange(at, [first, new CodeInstruction(OpCodes.Ldc_I4, k), new CodeInstruction(OpCodes.Call, hook)]);
            }
            return code;
        }
        public static void BeforeValueEvent(BeamEmitter emitter, int point)
        {
            Session? s = _active;
            if (s == null || Environment.CurrentManagedThreadId != s.Thread) return;
            try
            {
                if (point == 0) s.Current = emitter;
                if (s.Valid && !Admitted(emitter, out string? reason)) Invalidate(reason);
                if (point == 1) s.Current = null;
            }
            catch (Exception ex) { Invalidate("listener check " + ex.GetType().Name); }
        }
    }

    internal static class TakeHook
    {
        public static bool Prefix(BeamEmitter __instance, V worldSpawnLoc, Direction worldSpawnDir, bool isInit, ref HitInfo __result, out Record? __state)
        {
            __state = null;
            Session? s = _active;
            if (s == null || Environment.CurrentManagedThreadId != s.Thread) return true;
            try { return !TryTake(s, __instance, worldSpawnLoc, worldSpawnDir, isInit, ref __result, out __state); }
            catch (Exception ex) { Invalidate("take " + ex.GetType().Name); return true; }
        }
        public static void Postfix(HitInfo __result, Record? __state)
        {
            if (__state != null) Compare(__state, __result);
        }
    }

    // One class per target: no injected __originalMethod and no allocation on the hot DoEffect path outside the window.
    internal static class StopHook { public static void Prefix() { if (_active != null) Invalidate("StopEmission"); } }
    internal static class AttenuateHook { public static void Prefix() { if (_active != null) Invalidate("AttenuateBeam"); } }
    internal static class DieHook { public static void Prefix() { if (_active != null) Invalidate("BaseBulletDeath.Die"); } }
    internal static class EffectHook { public static void Prefix() { if (_active != null) Invalidate("immediate MultiHitEffectRules.DoEffect"); } }

    // ---------------------------------------------------------------------------------------------------------------------
    // Snapshot (main thread)

    internal static bool SinglePlayer(SimRoot sim) => sim.Game is { } game && game.GetType() == typeof(GameRoot) && RuntimeType(game.NetManager) == typeof(SPManager);
    /// <summary>Engine annotations say non-null where runtime attachment fields can be null; read the type defensively.</summary>
    private static Type? RuntimeType(object? value) => value?.GetType();

    private static bool Owned(SimRoot sim)
    {
        if (sim.GetType() != typeof(SimRoot) || !SinglePlayer(sim) || sim.IsDoingParallelUpdate) return false;
        if (!ReferenceEquals(sim.SceneThread, Thread.CurrentThread)) return false;
        if (!_allowTestOwner && !ReferenceEquals(_director?.MainThread, Thread.CurrentThread)) return false;
        return RuntimeType(sim.Mode) == typeof(CreativeSimModeManager) && RuntimeType(sim.Game!.Mode) == typeof(CreativeGameModeManager) &&
            RuntimeType(sim.PhysicsWorld) == typeof(World) && RuntimeType(sim.PhysicsWorld.ContactManager) == typeof(ContactManager) &&
            RuntimeType(sim.PhysicsWorld.ContactManager.BroadPhase) == typeof(DynamicTreeBroadPhase<BodyFixtureProxy>);
    }

    internal static Session? Begin(SimRoot sim)
    {
        if (!Owned(sim)) { Stats.Fallback("session: not owned single-player creative sim"); return null; }
        if (_dispatchOverride == null && (!FastParallel.IsRunning || FastParallel.ThreadCount <= 0)) { Stats.Fallback("session: FastParallel unavailable"); return null; }
        List<IFixedUpdateableSceneObject> bucket = sim.FixedUpdateables.GetBucket(-4);
        _managers.Clear();
        foreach (IFixedUpdateableSceneObject member in bucket)
        {
            if (RuntimeType(member) == typeof(PartsManager.FixedUpdateCallbacks) && OcclusionOnly((PartsManager.FixedUpdateCallbacks)member)) continue;
            if (member is not CustomFixedUpdateProxy proxy || proxy.GetType() != typeof(CustomFixedUpdateProxy) || proxy._callback is not { } callback ||
                callback.Target is not WeaponManager manager || manager.GetType() != typeof(WeaponManager) || !SameMethod(callback.Method, EmitBeamsMethod))
            {
                _managers.Clear();
                Stats.Fallback(UnknownMember(member));
                return null;
            }
            _managers.Add(manager);
        }
        try { return BeginWith(sim, _managers); }
        finally { _managers.Clear(); }
    }
    private static readonly List<WeaponManager> _managers = [];
    private static readonly MethodInfo OcclusionCallback = AccessTools.Method(typeof(PartTileLineScoreValue.LineOcclusionHandler), "OnFixedUpdate");

    /// <summary>
    /// Bucket -4 also holds per-ship part callbacks. The only native one is the tile-line occlusion check
    /// (PartTileLineScoreValue.cs:192-363): a main-thread raycast with its own callback, a deferred EnqueueDeterministic,
    /// non-deterministic media and its own timer. It writes nothing a beam query reads, so it is not a barrier.
    /// </summary>
    private static bool OcclusionOnly(PartsManager.FixedUpdateCallbacks callbacks)
    {
        foreach (SceneComponent.FixedUpdateCallback callback in callbacks.Callbacks)
            if (RuntimeType(callback) == null || RuntimeType(callback.Target) != typeof(PartTileLineScoreValue.LineOcclusionHandler) || !callback.HasSingleTarget || !SameMethod(callback.Method, OcclusionCallback)) return false;
        return true;
    }
    private static readonly Dictionary<(Type?, MethodInfo?), string> MemberNames = new();
    private static string UnknownMember(IFixedUpdateableSceneObject? member)
    {
        Delegate? callback = (member as CustomFixedUpdateProxy)?._callback;
        var key = (member?.GetType(), callback?.Method);
        if (!MemberNames.TryGetValue(key, out string? name))
            MemberNames[key] = name = "session: unknown bucket -4 member " + key.Item1?.FullName + " " + callback?.Target?.GetType().FullName + "." + callback?.Method.Name;
        return name;
    }

    /// <summary>Snapshot and speculative round for the EmitBeams managers of bucket -4, in bucket order.</summary>
    internal static Session? BeginWith(SimRoot sim, List<WeaponManager> managers)
    {
        if (!Owned(sim)) { Stats.Fallback("session: not owned single-player creative sim"); return null; }
        long start = Stopwatch.GetTimestamp();
        Session s = Pooled;
        s.Clear();
        s.Sim = sim; s.Thread = Environment.CurrentManagedThreadId; s.Valid = true;
        _active = s;
        foreach (WeaponManager manager in managers)
            if (!PredictShip(s, manager)) break;
        long snap = Stopwatch.GetTimestamp();
        foreach (Record r in s.Records) if (r.Eligible) s.Work.Add(r);
        // Heaviest first (secondary rays); stable, so the order never depends on the worker count.
        s.Work.Sort((a, b) => a.SecondaryWidth.HasValue != b.SecondaryWidth.HasValue ? (a.SecondaryWidth.HasValue ? -1 : 1) : a.Order.CompareTo(b.Order));
        if (s.Work.Count > 0)
        {
            if (_dispatchOverride is { } dispatch) dispatch(s.Work.Count, i => Run(s.Work[i]));
            else FastParallel.For(0, s.Work.Count, ParallelBody, s, batchSize: 1);
        }
        Stats.Begin(s, Stopwatch.GetTimestamp() - start, snap - start);
        return s;
    }

    private static readonly FastParallelAction ParallelBody = (from, to, data) =>
    {
        var s = (Session)data!;
        for (int i = from; i < to; i++) Run(s.Work[i]);
    };

    private static FastRand RentRand(ulong a, ulong b)
    {
        FastRand r = RandPool.Count > 0 ? RandPool.Pop() : new FastRand(0uL, 0uL);
        r.SetState(a, b);
        return r;
    }

    /// <summary>Predicts this ship's beams in native order; false stops prediction for the rest of the tick.</summary>
    private static bool PredictShip(Session s, WeaponManager manager)
    {
        if (manager.SuppressAllWeapons || manager._emittingBeams.Count <= 0) return true;
        Ship ship = manager.Ship;
        if (RuntimeType(ship) != typeof(Ship) || RuntimeType(ship.Rand) != typeof(FastRand) || !ReferenceEquals(ship._root, s.Sim)) { Stats.Fallback("predict stop: ship"); return false; }
        ship.Rand.GetState(out ulong a, out ulong b);
        FastRand rand = RentRand(a, b);
        s.Predicted[ship] = rand;
        foreach (BeamEmitter beam in manager._emittingBeams)
        {
            switch (PredictBeam(s, ship, beam, rand))
            {
                case Prediction.Next: continue;
                default: return false;
            }
        }
        return true;
    }

    private enum Prediction { Next, StopTick }

    private static Prediction PredictBeam(Session s, Ship ship, BeamEmitter beam, FastRand rand)
    {
        if (RuntimeType(beam) != typeof(BeamEmitter) || RuntimeType(beam.Rules) != typeof(BeamEmitterRules) || RuntimeType(beam.Part) != typeof(Part) ||
            !ReferenceEquals(beam.Part!.Ship, ship) || !ReferenceEquals(beam.Sim, s.Sim)) { Stats.Fallback("predict stop: emitter shape"); return Prediction.StopTick; }
        // Line 832: an unadmitted listener runs before this beam's query; nothing after it can be speculated.
        if (!Admitted(beam, out string? reason)) { Stats.Fallback("predict stop: ", reason); return Prediction.StopTick; }
        if (!beam.IsToggleOn) return Prediction.Next; // 833-836, no draw
        BeamEmitterRules rules = beam.Rules;
        if (beam._emissionTimeRemaining is not { } remaining) { Stats.Fallback("predict: no emission time"); return Prediction.StopTick; }
        if (remaining <= 0f)
        {
            // 837-842: either StopEmission (a barrier) or the reset-delay branch, which needs the native fire checks.
            Stats.Fallback(rules.InitHitLengthResetDelay > Halfling.Timing.Time.Zero ? "predict stop: reset delay gate" : "predict stop: StopEmission");
            return Prediction.StopTick;
        }
        // Every implementation the calls below dispatch to is vetted first; an unknown one is never invoked.
        if (!SpawnGraphVetted(beam, out string? spawnWhy)) { Stats.Fallback("predict stop: ", spawnWhy); return Prediction.StopTick; }
        float effectsScale = beam._effectsScale; // 843-847 recompute natively; replay compares the field
        V point = beam.GetWorldSpawnLoc();
        Angle min = rules.Spread.Min.GetValue(beam.Part, effectsScale);
        Angle max = rules.Spread.Max.GetValue(beam.Part, effectsScale);
        Angle angle = rand.Angle(min, max);
        Direction direction = beam.GetWorldSpawnDir() + angle;
        float initialHitLength = beam.InitialHitLength;
        bool isInit = initialHitLength <= 0f;
        if (!QueryGraphVetted(beam, s.Sim, out string? queryWhy))
        {
            // This beam's native query will call an unaudited getter or modifier, which may change what later queries read.
            Stats.Fallback("predict stop: ", queryWhy);
            return Prediction.StopTick;
        }

        Record r = RecordPool.Count > 0 ? RecordPool.Pop() : new Record();
        r.Emitter = beam; r.Rules = rules; r.Part = beam.Part; r.Ship = ship; r.Sim = s.Sim; r.World = s.Sim.PhysicsWorld;
        r.Order = s.Records.Count; r.Spawn = point; r.Dir = direction; r.IsInit = isInit; r.LengthField = beam._length;
        r.EffectsScale = effectsScale; r.InitHitObjectId = beam._initHitObjectID;
        r.Outcome = Outcome.Pending; r.Reason = null; r.Info = default; r.QueryTicks = 0; r.Taken = false; r.Eligible = false;
        s.Records.Add(r);
        s.ByEmitter[beam] = r;
        if (!Lines1055To1079(beam, point, direction, effectsScale, out float currentRange, out float infoLength, out V spawnDirVector,
            out V preOffsetEnd, out V worldOrigin, out Circle? offset))
        { r.Reason = "fire target"; Stats.Fallback("predict stop: fire target"); return Prediction.StopTick; }
        r.CurrentRange = currentRange; r.InfoLength = infoLength; r.SpawnDirVector = spawnDirVector; r.PreOffsetEnd = preOffsetEnd; r.WorldOrigin = worldOrigin;
        r.EndPoint = offset is { } circle ? rand.PointInside(circle) : preOffsetEnd;
        if (!ReadQueryValues(beam, effectsScale, out r.RangeValue, out r.SecondaryWidth, out r.ShipTarget, out r.ActionSource, out r.SourcePlayer, out r.ShipPlayer, out r.MaxStructuralPenetrations))
        { Stats.Fallback("predict stop: query values"); return Prediction.StopTick; }
        // Unaudited filter, validator or shape: its native query runs unaudited code before later beams.
        if (!StaticEligible(beam, out string? why)) { Stats.Fallback("predict stop: ", why); return Prediction.StopTick; }
        r.Eligible = true;
        return Prediction.Next;
    }

    private static readonly HashSet<Type> AuditedModifiers =
    [
        typeof(BuffModifier), typeof(ScaledBuffModifier), typeof(BuffRemapModifier), typeof(EffectScaleModifier), typeof(ScaledEffectScaleModifier),
    ];
    /// <summary>Exact ModifiableFloat/ModifiableAngle whose modifiers only read buffs and the effect scale (no status sources).</summary>
    internal static bool SafeModifiable(object? value)
    {
        ValueModifier[]? modifiers;
        if (RuntimeType(value) == typeof(ModifiableFloat)) modifiers = ((ModifiableFloat)value!).Modifiers;
        else if (RuntimeType(value) == typeof(ModifiableAngle)) modifiers = ((ModifiableAngle)value!).Modifiers;
        else return false;
        if (modifiers == null) return true;
        foreach (ValueModifier modifier in modifiers)
            if (RuntimeType(modifier) is not { } type || !AuditedModifiers.Contains(type)) return false;
        return true;
    }

    /// <summary>GetWorldSpawnLoc/Dir (chained transforms) and the spread values, before any of them is called.</summary>
    private static bool SpawnGraphVetted(BeamEmitter beam, [NotNullWhen(false)] out string? why)
    {
        why = null;
        ChainablePartComponent? chained = beam.ChainedComponent;
        for (int depth = 0; chained != null && why == null; depth++, chained = chained.ChainedComponent)
            if (depth >= 8 || chained.GetType() != typeof(FixedWeapon)) why = "chained component";
        if (why == null && (!SafeModifiable(beam.Rules.Spread.Min) || !SafeModifiable(beam.Rules.Spread.Max))) why = "spread modifier";
        return why == null;
    }

    private static readonly BeamTargetMemo.Query TargetQuery = new();
    /// <summary>
    /// GetCurrentRange, GetFireTarget/GetShipTarget and the rule values of lines 1055-1079 and the query, before any of them is
    /// called. The target getters dispatch through Emitter._getTargetFrom; only the exact FixedWeapon graph that the beam
    /// target memo audit accepts (no lazy sight or bounding refresh, exact targets, commands and filters) is called.
    /// </summary>
    internal static bool QueryGraphVetted(BeamEmitter beam, SimRoot sim, [NotNullWhen(false)] out string? why)
    {
        BeamEmitterRules rules = beam.Rules;
        why = null;
        if (rules.StretchRange != null) why = "stretch range";
        else if (!SafeModifiable(rules.Range) || (rules.SecondaryWidth != null && !SafeModifiable(rules.SecondaryWidth)) ||
            (rules.RandomOffsetRadius is { } radius && (!SafeModifiable(radius.Min) || !SafeModifiable(radius.Max)))) why = "range modifier";
        else if (beam._getTargetFrom != null)
        {
            BeamTargetMemo.Query q = TargetQuery;
            q.Clear();
            q.Receiver = beam; q.Provider = beam._getTargetFrom; q.Sim = sim; q.World = sim.PhysicsWorld;
            try { if (!BeamTargetMemo.PureBefore(q, secondaryFilter: false)) why = "target graph"; }
            finally { q.Clear(); }
        }
        return why == null;
    }

    /// <summary>Native lines 1055-1079 without the offset draw (read-only on the main thread).</summary>
    internal static bool Lines1055To1079(BeamEmitter beam, V worldSpawnLoc, Direction worldSpawnDir, float effectsScale,
        out float currentRange, out float infoLength, out V spawnDirVector, out V endPoint, out V worldOrigin, out Circle? offset)
    {
        BeamEmitterRules rules = beam.Rules;
        currentRange = beam.GetCurrentRange();
        infoLength = currentRange;
        if (rules.ClipRangeToTarget)
        {
            ITarget? fireTarget = beam.GetFireTarget(checkProhibited: false);
            if (fireTarget != null && fireTarget.ExistsInSim(beam.Sim))
            {
                float value = worldSpawnLoc.DistanceTo(fireTarget.DetWorldCenter);
                infoLength = Mathx.Min(infoLength, value);
            }
        }
        spawnDirVector = V.FromAngle(worldSpawnDir);
        endPoint = worldSpawnLoc + spawnDirVector * infoLength;
        worldOrigin = rules.AntiPhasingRaycastDistance > 0f ? worldSpawnLoc - spawnDirVector * rules.AntiPhasingRaycastDistance : worldSpawnLoc;
        offset = null;
        if (rules.RandomOffsetRadius.HasValue)
        {
            float t = Mathx.InverseLerp(0f, currentRange, infoLength);
            float radius = Mathx.Lerp(rules.RandomOffsetRadius.Value.Min.GetValue(beam.Part, effectsScale), rules.RandomOffsetRadius.Value.Max.GetValue(beam.Part, effectsScale), t);
            offset = new Circle(in endPoint, radius);
        }
        return true;
    }

    /// <summary>Values the native query reads from rules, targets and the sim while it runs (main thread).</summary>
    internal static bool ReadQueryValues(BeamEmitter beam, float effectsScale, out float range, out float? secondaryWidth, out Ship? target,
        out ActionSourcePlayer? source, out int sourcePlayer, out int shipPlayer, out int maxPenetrations)
    {
        BeamEmitterRules rules = beam.Rules;
        range = rules.Range.GetValue(beam.Part, effectsScale);
        secondaryWidth = rules.SecondaryWidth?.GetValue(beam.Part, effectsScale);
        target = beam.GetShipTarget(checkProhibited: true);
        source = beam.Sim!.CurrentActionSourcePlayer;
        shipPlayer = beam.Ship!.Metadata.PlayerIndex;
        sourcePlayer = source?.PlayerId ?? shipPlayer;
        maxPenetrations = rules.HitStructural?.MaxStructuralPenetrations ?? int.MaxValue;
        return target == null || target.GetType() == typeof(Ship);
    }

    /// <summary>Exact audited shapes of everything the worker query reads that is fixed per beam.</summary>
    internal static bool StaticEligible(BeamEmitter beam, [NotNullWhen(false)] out string? why)
    {
        BeamEmitterRules rules = beam.Rules;
        why = null;
        if (rules.ColliderOverlapTest) why = "collider overlap test";
        else if (!NativeCallback(beam._validateHit, NativeValidateHit, beam)) why = "hit validator";
        else if (!SafeFilter(rules.ShieldCollisions) || !SafeFilter(rules.AttenuatorCollisions) || !SafeFilter(rules.SecondaryFilter) ||
            (rules.SecondaryOverrideFilter != null && !SafeFilter(rules.SecondaryOverrideFilter))) why = "filter shape";
        else if (rules.SecondaryWidth != null && rules.SecondaryWidth.GetType() != typeof(ModifiableFloat)) why = "secondary width type";
        else if (RuntimeType(rules.Range) != typeof(ModifiableFloat)) why = "range type";
        else if (RuntimeType(beam.Ship?.Physics) != typeof(ShipPhysics) || RuntimeType(beam.Ship!.Metadata) == null || RuntimeType(beam.Sim) == null) why = "ship shape";
        else if (!PlayerKnown(beam.Sim!, beam.Ship.Metadata.PlayerIndex) || !PlayerKnown(beam.Sim!, beam.Sim!.CurrentActionSourcePlayer?.PlayerId ?? beam.Ship.Metadata.PlayerIndex)) why = "player state";
        return why == null;
    }
    internal static bool SameMethod(MethodInfo a, MethodInfo b) => a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    private static bool NativeCallback(Delegate? callback, MethodInfo method, object target) =>
        callback is { HasSingleTarget: true } && ReferenceEquals(callback.Target, target) && SameMethod(callback.Method, method);
    private static bool SafeSet(HashSet<ID<PartCategory>>? set) => set == null ||
        (set.GetType() == typeof(HashSet<ID<PartCategory>>) && ReferenceEquals(set.Comparer, EqualityComparer<ID<PartCategory>>.Default));
    internal static bool SafeFilter(EffectFilter? filter) => filter != null && filter.GetType() == typeof(EffectFilter) &&
        filter.OnlyStatuses is null or { Length: 0 } && filter.ExcludeStatuses is null or { Length: 0 } &&
        filter.OnlyBulletCategories == null && filter.ExcludeBulletCategories == null &&
        SafeSet(filter.OnlyPartCategories) && SafeSet(filter.ExcludePartCategories);

    /// <summary>
    /// The allegiance checks resolve players through GameRoot.GetPlayerState, which adds a missing player (a write).
    /// Indexes -1..-4 short-circuit before the lookup in SimModeManager.AreEnemies/AreAllies.
    /// </summary>
    internal static bool PlayerKnown(SimRoot sim, int player) => player is -1 or -2 or -3 or -4 ||
        (sim.Game is { } game && game._playerStates.GetType() == typeof(SortedDictionary<int, PlayerState>) &&
         ReferenceEquals(game._playerStates.Comparer, Comparer<int>.Default) && game._playerStates.ContainsKey(player));

    // ---------------------------------------------------------------------------------------------------------------------
    // Listener admission (lines 832, 948; HitIntervalElapsed at 929)

    private static readonly ConditionalWeakTable<PartMultiValue, object> PullReaderFree = new();

    internal static bool Admitted(BeamEmitter beam, [NotNullWhen(false)] out string? reason)
    {
        reason = null;
        if (BeamTrigger(beam) != null) { reason = "trigger subscriber"; return false; }
        EventHandler<EventArgs>? listeners = BeamValueChanged(beam);
        if (listeners == null) return true;
        foreach (EventHandler<EventArgs> listener in Delegate.EnumerateInvocationList(listeners))
        {
            object? target = listener.Target;
            if (target?.GetType() == typeof(PartMultiValue) && SameMethod(listener.Method, MultiValueListener))
            {
                if (!AdmittedMultiValue((PartMultiValue)target, out reason)) return false;
            }
            else if (target?.GetType() == typeof(BeamEmitterProxy) && SameMethod(listener.Method, ProxyListener))
            {
                var proxy = (BeamEmitterProxy)target;
                if (ProxyValueChanged(proxy) != null) { reason = "proxy downstream subscriber"; return false; }
                if (!ProxyChain(proxy)) { reason = "proxy chain"; return false; }
            }
            else { reason = UnknownListener(target, listener.Method); return false; }
        }
        return true;
    }

    private static readonly Dictionary<(Type?, MethodInfo), string> ListenerNames = new();
    private static string UnknownListener(object? target, MethodInfo method)
    {
        if (!ListenerNames.TryGetValue((target?.GetType(), method), out string? name))
            ListenerNames[(target?.GetType(), method)] = name = "unknown value listener " + target?.GetType().Name + "." + method.Name;
        return name;
    }
    private static bool AdmittedMultiValue(PartMultiValue value, [NotNullWhen(false)] out string? reason)
    {
        reason = null;
        if (MultiValueChanged(value) != null) reason = "multi-value downstream subscriber";
        else if (value._viaBuffs != null) reason = "multi-value via buffs";
        else if (RuntimeType(value.Part) == null || RuntimeType(value.Part!.Rules) == null || RuntimeType(value.Rules) == null || value.Part.Rules.ID != PrismPart || value.Rules.ID != PrismValue) reason = "multi-value outside audited prism wiring";
        else if (value._values == null) reason = "multi-value sources";
        else
        {
            foreach (IPartComponentValue? source in value._values)
            {
                if (source?.GetType() == typeof(BeamEmitter)) continue;
                if (source?.GetType() == typeof(BeamEmitterProxy) && ProxyChain((BeamEmitterProxy)source)) continue;
                reason = "multi-value source type";
                return false;
            }
            if (!PullReaderFree.TryGetValue(value, out _))
            {
                if (!NoQueryPullReader(value)) { reason = "multi-value pull reader"; return false; }
                PullReaderFree.AddOrUpdate(value, Boxed);
            }
        }
        return reason == null;
    }
    private static readonly object Boxed = new();
    internal static void ResetPullReaderCache(PartMultiValue value) => PullReaderFree.Remove(value);

    /// <summary>BeamEmitterProxy.Value forwards through IBeamEmitter.Value: only exact proxies ending in an exact BeamEmitter or nothing.</summary>
    internal static bool ProxyChain(BeamEmitterProxy proxy)
    {
        BeamEmitterProxy current = proxy;
        for (int depth = 0; depth < 8; depth++)
        {
            if (RuntimeType(current._proxy) != typeof(ProxyHandler<IBeamEmitter>)) return false;
            IBeamEmitter? next = current._proxy.ProxiedComponent;
            if (next == null || next.GetType() == typeof(BeamEmitter)) return true;
            if (next.GetType() != typeof(BeamEmitterProxy) || ReferenceEquals(next, proxy)) return false;
            current = (BeamEmitterProxy)next;
        }
        return false; // cycle or unbounded chain
    }

    /// <summary>
    /// Pull readers: within its part, only the audited PartDeathEffects (read at part death, outside bucket -4) may hold the
    /// multi-value. The value's own ValueChanged is null (push readers), and via-buffs are excluded.
    /// </summary>
    internal static bool NoQueryPullReader(PartMultiValue value)
    {
        if (value.Part?.Components is not { } components) return false;
        foreach (PartComponent component in components)
        {
            if (ReferenceEquals(component, value)) continue;
            if (!References(component, value)) continue;
            if (component.GetType() != typeof(PartDeathEffects)) return false;
        }
        return true;
    }
    private static readonly Dictionary<Type, FieldInfo[]> ReaderFields = new();
    private static bool References(object holder, object value)
    {
        Type type = holder.GetType();
        if (!ReaderFields.TryGetValue(type, out FieldInfo[]? fields))
        {
            var list = new List<FieldInfo>();
            for (Type? t = type; t != null && t != typeof(object); t = t.BaseType)
                list.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(f => !f.FieldType.IsValueType));
            ReaderFields[type] = fields = list.ToArray();
        }
        foreach (FieldInfo field in fields)
        {
            object? content = field.GetValue(holder);
            if (ReferenceEquals(content, value)) return true;
            if (content is Array array && !array.GetType().GetElementType()!.IsValueType)
                foreach (object? item in array) if (ReferenceEquals(item, value)) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // Replay (main thread, GetHitInfo prefix)

    internal static bool TryTake(Session s, BeamEmitter beam, V worldSpawnLoc, Direction worldSpawnDir, bool isInit, ref HitInfo result, out Record? verify)
    {
        verify = null;
        if (!s.ByEmitter.TryGetValue(beam, out Record? r)) return false;
        if (!s.Valid) { Stats.Miss(r, "after barrier"); return false; }
        if (!ReferenceEquals(s.Current, beam) || r.Taken) { Invalidate("unexpected GetHitInfo call"); return false; }
        r.Taken = true; // per-emitter invocation ordinal 0 only
        if (r.Outcome == Outcome.Attenuates) { Invalidate("attenuation"); Stats.Miss(r, "attenuation"); return false; }
        // v1: any worker stop runs this beam natively, and that native query can go past the first rejected object into
        // shapes, getters or lazy caches no worker inspected. Every later record is invalidated before it runs.
        if (r.Outcome != Outcome.Ready)
        {
            Stats.LostToFallback += s.Records.Count(x => x.Order > r.Order && x.Outcome == Outcome.Ready && !x.Taken);
            Invalidate("native fallback");
            Stats.Miss(r, r.Reason ?? "native");
            return false;
        }
        if (!QueryGraphVetted(beam, s.Sim, out string? why)) { Stats.Miss(r, why); Invalidate("call graph changed"); return false; }
        string? mismatch = Mismatch(r, beam, worldSpawnLoc, worldSpawnDir, isInit, out Circle? offset);
        if (mismatch != null) { Stats.Miss(r, mismatch); Invalidate("input mismatch"); return false; }
        if (offset is { } circle)
        {
            FastRand rand = beam.Ship!.Rand;
            rand.GetState(out ulong a, out ulong b);
            V drawn;
            if (Verify)
            {
                FastRand copy = RentRand(a, b);
                drawn = copy.PointInside(circle);
                RandPool.Push(copy);
            }
            else drawn = rand.PointInside(circle);
            if (!Same(drawn, r.EndPoint)) { if (!Verify) rand.SetState(a, b); Stats.Miss(r, "input offset draw"); Invalidate("input mismatch"); return false; }
        }
        if (Verify) { verify = r; return false; } // native runs; the postfix compares
        beam._tempWorldSpawnLoc = worldSpawnLoc; // line 1080
        result = r.Info;
        r.Info = default; // ownership of AllStructuralHits passes to the native Dispose at line 954
        Stats.Accept(r);
        return true;
    }

    private static string? Mismatch(Record r, BeamEmitter beam, V worldSpawnLoc, Direction worldSpawnDir, bool isInit, out Circle? offset)
    {
        offset = null;
        if (!ReferenceEquals(beam.Part, r.Part) || !ReferenceEquals(beam.Ship, r.Ship) || !ReferenceEquals(beam.Sim, r.Sim) || !ReferenceEquals(r.Sim.PhysicsWorld, r.World) ||
            !ReferenceEquals(beam.Rules, r.Rules) || !ReferenceEquals(beam.Ship.Rand, beam.Part.Ship!.Rand)) return "identity";
        if (!Same(worldSpawnLoc, r.Spawn)) return "spawn";
        if (!Same(worldSpawnDir, r.Dir)) return "direction";
        if (isInit != r.IsInit) return "isInit";
        if (!Same(beam._length, r.LengthField)) return "length";
        if (!Same(beam._effectsScale, r.EffectsScale)) return "scale";
        if (beam._initHitObjectID != r.InitHitObjectId) return "initHitObjectID";
        if (!Lines1055To1079(beam, worldSpawnLoc, worldSpawnDir, beam._effectsScale, out float currentRange, out float infoLength, out V spawnDirVector,
            out V endPoint, out V worldOrigin, out offset)) return "fire target";
        if (!Same(currentRange, r.CurrentRange) || !Same(infoLength, r.InfoLength)) return "range";
        if (!Same(spawnDirVector, r.SpawnDirVector) || !Same(endPoint, r.PreOffsetEnd) || !Same(worldOrigin, r.WorldOrigin)) return "end point";
        if (offset == null && !Same(endPoint, r.EndPoint)) return "end point";
        if (!ReadQueryValues(beam, beam._effectsScale, out float range, out float? secondaryWidth, out Ship? target, out ActionSourcePlayer? source,
            out int sourcePlayer, out int shipPlayer, out int maxPenetrations)) return "target type";
        if (!Same(range, r.RangeValue) || secondaryWidth.HasValue != r.SecondaryWidth.HasValue || (secondaryWidth.HasValue && !Same(secondaryWidth.Value, r.SecondaryWidth!.Value))) return "rule values";
        if (!ReferenceEquals(target, r.ShipTarget)) return "target";
        if (!ReferenceEquals(source, r.ActionSource) || sourcePlayer != r.SourcePlayer || shipPlayer != r.ShipPlayer) return "source player";
        if (maxPenetrations != r.MaxStructuralPenetrations) return "penetrations";
        if (!NativeCallback(beam._validateHit, NativeValidateHit, beam)) return "hit validator";
        return null;
    }
    private static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    /// <summary>Verify mode: the speculative result against the native one, bit for bit, including the structural hit order.</summary>
    internal static string? Difference(in HitInfo expected, in HitInfo actual)
    {
        if (!Same(expected.Length, actual.Length)) return "Length";
        if (!Same(expected.EndPoint, actual.EndPoint)) return "EndPoint";
        if (!Same(expected.Operational.WorldPoint, actual.Operational.WorldPoint) || !Same(expected.Operational.WorldNormal, actual.Operational.WorldNormal)) return "Operational point";
        if (!SameHit(expected.Operational.Hit, actual.Operational.Hit)) return "Operational hit";
        if (!SameStructural(expected.Structural, actual.Structural)) return "Structural";
        if ((expected.AllStructuralHits == null) != (actual.AllStructuralHits == null)) return "AllStructuralHits presence";
        if (expected.AllStructuralHits != null)
        {
            if (expected.AllStructuralHits.Count != actual.AllStructuralHits!.Count) return "AllStructuralHits count";
            for (int i = 0; i < expected.AllStructuralHits.Count; i++)
                if (!SameStructural(expected.AllStructuralHits[i], actual.AllStructuralHits[i])) return "AllStructuralHits[" + i + "]";
        }
        return null;
    }
    private static bool SameHit(HitObject a, HitObject b) => ReferenceEquals(a.Part, b.Part) && ReferenceEquals(a.Collider, b.Collider) &&
        ReferenceEquals(a.Shield, b.Shield) && ReferenceEquals(a.Crew, b.Crew) && ReferenceEquals(a.Attenuator, b.Attenuator);
    private static bool SameStructural(StructuralHitInfo a, StructuralHitInfo b) => ReferenceEquals(a.Part, b.Part) && ReferenceEquals(a.Collider, b.Collider) &&
        Same(a.WorldPoint, b.WorldPoint) && Same(a.WorldNormal, b.WorldNormal);
    private static void Compare(Record r, in HitInfo native)
    {
        string? difference = Difference(r.Info, native);
        if (difference == null)
        {
            Stats.Verified++;
            if (native.Operational.Hit.Shield != null) Stats.VerifiedShield++;
            if (native.AllStructuralHits is { Count: > 1 }) Stats.VerifiedPenetrating++;
            if (r.SecondaryWidth is > 0f) Stats.VerifiedSecondary++;
        }
        else
        {
            Stats.VerifyMismatches++;
            Stats.Fallback("VERIFY MISMATCH " + difference);
            if (Stats.VerifyMismatches <= 20)
                Log.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Beam parallel verify mismatch ({difference}): order {r.Order} secondary {r.SecondaryWidth.HasValue} spec len {r.Info.Length} end {r.Info.EndPoint} native len {native.Length} end {native.EndPoint}"));
        }
    }
    private static bool Same(V a, V b) => Same(a.X, b.X) && Same(a.Y, b.Y);

    internal static void End(Session s)
    {
        foreach (Record r in s.Records)
        {
            if (r.Outcome == Outcome.Ready && !r.Taken) Stats.Waste(r);
            r.Info.Dispose(); // unused results only: taken records were cleared
            r.Info = default;
            r.Emitter = null!; r.Rules = null!; r.Part = null!; r.Ship = null!; r.Sim = null!; r.World = null!; r.ShipTarget = null; r.ActionSource = null;
            RecordPool.Push(r);
        }
        foreach (FastRand rand in s.Predicted.Values) RandPool.Push(rand);
        Stats.OnEnd();
        s.Clear();
        if (ReferenceEquals(_active, s)) _active = null;
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // Worker query (any thread). Reads frozen state only; writes only its record and its own thread's context.

    internal static void Run(Record r) => Run(r, frozen: false);

    /// <summary>Guarded worker run. <paramref name="frozen"/>: an attenuation is recorded on the record (BeamFrozen) instead of stopping.</summary>
    internal static void Run(Record r, bool frozen)
    {
        long start = Stopwatch.GetTimestamp();
        Ctx c = Ctx.Current;
        try
        {
            c.Rec = r; c.Abort = null; c.Frozen = frozen; r.Attenuator = null;
            r.Outcome = c.Query(out r.Info);
            if (r.Outcome != Outcome.Ready) { r.Reason = c.Abort ?? r.Reason; r.Info.Dispose(); r.Info = default; r.Attenuator = null; }
        }
        catch (Exception ex)
        {
            r.Info.Dispose(); r.Info = default; r.Attenuator = null;
            r.Outcome = Outcome.Native; r.Reason = "exception " + ex.GetType().Name;
        }
        finally { c.Release(); } // no scene object stays reachable from worker thread-local state
        r.QueryTicks = Stopwatch.GetTimestamp() - start;
    }

    /// <summary>
    /// BeamFrozen serial query (main thread only, after the worker join): the same query with every worker guard off, so unknown
    /// shapes, filters, crew types, cold shield caches and missing players take the native paths. Exceptions propagate as native ones.
    /// </summary>
    internal static void RunSerial(Record r)
    {
        long start = Stopwatch.GetTimestamp();
        Ctx c = Ctx.Current;
        r.Info.Dispose(); r.Info = default; r.Attenuator = null;
        try
        {
            c.Rec = r; c.Abort = null; c.Frozen = true; c.Serial = true;
            r.Outcome = c.Query(out r.Info);
            if (r.Outcome != Outcome.Ready) throw new InvalidOperationException("Beam frozen serial query stopped: " + c.Abort);
        }
        finally { c.Release(); }
        r.QueryTicks += Stopwatch.GetTimestamp() - start;
    }

    internal sealed class Ctx
    {
        [ThreadStatic] private static Ctx? _current;
        internal static Ctx Current => _current ??= new Ctx();
        internal Record Rec = null!;
        internal string? Abort;
        /// <summary>BeamFrozen: record the BE:1235 attenuation and continue as native instead of stopping.</summary>
        internal bool Frozen;
        /// <summary>BeamFrozen serial path (main thread): no worker guard; unknown objects take the native paths.</summary>
        internal bool Serial;
        private readonly Func<RayCastInput, Category, int, object, float> _broad, _fixturePhase;
        private readonly Func<Fixture, V, bool> _pointTest;
        private bool _data;
        private Fixture? _hitFixture;
        private V _hitPoint, _hitNormal;
        private HitObject _hitData;
        private Ctx() { _broad = Broad; _fixturePhase = FixturePhase; _pointTest = PointTest; }

        /// <summary>The first stop reason wins; the record then runs natively and invalidates every later record.</summary>
        private float Fail(string reason) { Abort ??= reason; return 0f; }
        internal void Release() { Rec = null!; Abort = null; Frozen = false; Serial = false; _hitFixture = null; _hitData = default; _hitPoint = default; _hitNormal = default; }
        /// <summary>BeamEmitter.ValidatePointTest (BE:1369-1377).</summary>
        private bool PointTest(Fixture fixture, V worldPoint)
        {
            NV point = worldPoint;
            return fixture.TestPoint(ref point) && ValidateHit(fixture, worldPoint);
        }
        internal bool Holds => RuntimeType(Rec) != null || _hitFixture != null || _hitData.Part != null || _hitData.Shield != null || _hitData.Collider != null || _hitData.Crew != null || _hitData.Attenuator != null;
        private static bool ShapeOk(Fixture? fixture)
        {
            Type? shape = RuntimeType(fixture?.Shape);
            return fixture?.GetType() == typeof(Fixture) && (shape == typeof(PolygonShape) || shape == typeof(CircleShape) || shape == typeof(EdgeShape));
        }

        /// <summary>World.RayCastCallbackBroadPhaseWrapper (World.cs:270-290) with the shape checked before native Shape.RayCast.</summary>
        private float Broad(RayCastInput rayCastInput, Category categories, int proxyId, object userData)
        {
            if (Abort != null) return 0f;
            BodyFixtureProxy proxy = Rec.World.ContactManager.BroadPhase.GetProxy(proxyId);
            if (proxy.Fixture != null)
            {
                Fixture fixture = proxy.Fixture;
                if (!Serial && (!ShapeOk(fixture) || fixture.Body?.GetType() != typeof(Body))) return Fail("shape");
                int childIndex = proxy.ChildIndex;
                if (fixture.RayCast(out RayCastOutput output, ref rayCastInput, childIndex))
                {
                    float fraction = output.Fraction;
                    NV arg = (1f - fraction) * rayCastInput.Point1 + fraction * rayCastInput.Point2;
                    return Callback(fixture, arg, output.Normal, fraction);
                }
                return rayCastInput.MaxFraction;
            }
            Body body = proxy.Body;
            if (!Serial && (body?.GetType() != typeof(Body) || body.FixtureTree?.GetType() != typeof(DynamicTree<BodyFixtureProxy>))) return Fail("body tree");
            rayCastInput.Point1 = Transform.Divide(rayCastInput.Point1, ref body._xf);
            rayCastInput.Point2 = Transform.Divide(rayCastInput.Point2, ref body._xf);
            body.FixtureTree!.RayCast(_fixturePhase, ref rayCastInput, categories, out float maxFraction, body);
            return maxFraction;
        }

        /// <summary>World.RayCastCallbackFixturePhaseWrapper (World.cs:292-308) with the shape checked before native Shape.RayCast.</summary>
        private float FixturePhase(RayCastInput rayCastInput, Category category, int proxyId, object userData)
        {
            if (Abort != null) return 0f;
            Body body = (Body)userData;
            BodyFixtureProxy userData2 = body.FixtureTree!.GetUserData(proxyId);
            Fixture fixture = userData2.Fixture;
            if (!Serial && !ShapeOk(fixture)) return Fail("shape");
            int childIndex = userData2.ChildIndex;
            Transform transform = Transform.Identity;
            if (fixture.Shape.RayCast(out RayCastOutput output, ref rayCastInput, ref transform, childIndex))
            {
                float fraction = output.Fraction;
                NV left = (1f - fraction) * rayCastInput.Point1 + fraction * rayCastInput.Point2;
                left = Transform.Multiply(left, ref body._xf);
                NV arg = Complex.Multiply(output.Normal, ref body._xf.q);
                return Callback(fixture, left, arg, fraction);
            }
            return rayCastInput.MaxFraction;
        }

        /// <summary>The PhysicsExtensions.RayCastFirst lambdas (PhysicsExtensions.cs:87-97, 114-126).</summary>
        private float Callback(Fixture fixture, NV point, NV normal, float fraction)
        {
            if (!_data)
            {
                bool accepted = ValidateHit(fixture, point);
                if (Abort != null) return 0f;
                if (accepted)
                {
                    _hitFixture = fixture; _hitNormal = normal; _hitPoint = point;
                    return fraction;
                }
                return -1f;
            }
            (bool flag, HitObject obj) = ValidateSecondaryHit(fixture, point);
            if (Abort != null) return 0f;
            if (flag)
            {
                _hitFixture = fixture; _hitNormal = normal; _hitPoint = point; _hitData = obj;
                return fraction;
            }
            return -1f;
        }

        /// <summary>World.RayCast (World.cs:1150-1160) through PhysicsExtensions.RayCastFirst.</summary>
        private void Cast(V start, V end, Category categories, float radius, bool data)
        {
            _data = data; _hitFixture = null; _hitPoint = V.Zero; _hitNormal = V.Zero; _hitData = default;
            RayCastInput input = default;
            input.MaxFraction = 1f;
            input.Point1 = start;
            input.Point2 = end;
            input.Radius = radius;
            Rec.World.ContactManager.BroadPhase.RayCast(_broad, ref input, categories, out _, null!);
        }

        // --- BeamEmitter.cs:1426-1541 with per-record spawn point, target and source player -------------------------

        private bool ShipOk(Ship? ship) => Serial || (RuntimeType(ship) == typeof(Ship) && RuntimeType(ship!.Metadata) != null && RuntimeType(ship.Physics) == typeof(ShipPhysics) &&
            PlayerKnown(Rec.Sim, ship.Metadata.PlayerIndex));
        /// <summary>
        /// ValidateShieldHit reads ArcAngle/ArcRadius (lazy caches, ArcShield.cs:143-171) and the chained RulesLocation/RulesRotation
        /// (lazy caches, ChainablePartComponent.cs:46-76). A cold cache would be written on a worker: that beam runs natively.
        /// </summary>
        internal static bool ShieldWarm(ArcShield shield) => shield._cachedArcAngle.HasValue && shield._cachedArcRadius.HasValue && shield._hasRulesLoc && shield._hasRulesRot;
        private void StopShield(ArcShield shield) => Fail(shield.GetType() == typeof(ArcShield) && !ShieldWarm(shield) ? "cold shield" : "shield");
        private void StopShip(Ship? ship) => Fail(RuntimeType(ship) == typeof(Ship) ? "player" : "hit ship");
        private bool ShieldOk(ArcShield shield) => Serial || (shield.GetType() == typeof(ArcShield) && ShieldWarm(shield) && (shield.Ship == null || ShipOk(shield.Ship)));

        /// <summary>
        /// Everything EffectFilter.ValidateHitCrew (EffectFilter.cs:444-467) reads before the native call, in its branch order:
        /// crew.Sim, the Crew flag, Soul.HomeShip, HomeShip.ExistsInSim (Ship.cs:1387, Parts.Count), then either the ship
        /// allegiance (ValidateAllegiance(Ship...), the same reads as a ship hit: ShipOk) or the player allegiance
        /// (ValidateAllegiance(SimRoot...) :557-572: Mode.CanDamage/AreEnemies, which add a missing player state: PlayerKnown).
        /// The source player of every caller is r.ShipPlayer or r.SourcePlayer, both checked at snapshot. Plain field and
        /// auto-property reads only; derived crew, soul or filter types stop.
        /// </summary>
        private string? CrewStop(SpaceCrew crew, EffectFilter? filter)
        {
            if (Serial) return null;
            if (RuntimeType(crew) != typeof(SpaceCrew)) return "crew type";
            if (RuntimeType(filter) != typeof(EffectFilter)) return "crew filter";
            SimRoot? sim = crew.Sim;
            if (sim == null || !filter!.Crew) return null; // native returns false before any other read
            if (!ReferenceEquals(sim, Rec.Sim) || RuntimeType(crew.Soul) != typeof(CrewSoul)) return "crew sim";
            Ship? home = crew.Soul.HomeShip;
            if (home != null)
            {
                if (RuntimeType(home) != typeof(Ship) || RuntimeType(home.Parts) != typeof(PartsManager)) return "crew home";
                if (home.ExistsInSim(sim)) return ShipOk(home) ? null : "crew home";
            }
            return PlayerKnown(sim, crew.Soul.PlayerIndex) ? null : "crew player";
        }
        private bool CrewOk(SpaceCrew crew, EffectFilter? filter)
        {
            string? stop = CrewStop(crew, filter);
            if (stop == null) return true;
            Fail(stop);
            return false;
        }

        private bool ValidateHit(Fixture fixture, V worldPoint)
        {
            Record r = Rec;
            if (fixture.UserData is ArcShield arcShield)
            {
                if (!ShieldOk(arcShield)) { StopShield(arcShield); return false; }
                if (r.Rules.ShieldCollisions.ValidateHitShield(arcShield, r.Ship, null, r.ShipTarget))
                {
                    return arcShield.ValidateShieldHit(worldPoint, (worldPoint - r.Spawn) * 10000f);
                }
                return false;
            }
            object? userData = fixture.Body?.UserData;
            if (!(userData is Ship hitShip))
            {
                if (!(userData is SpaceCrew))
                {
                    if (userData is BulletData bulletData && (Serial || bulletData.GetType() == typeof(BulletData)))
                    {
                        if (bulletData.BeamAttenuator is BulletComponent bulletComponent)
                        {
                            if (!Serial && (bulletComponent.GetType() != typeof(BulletVolumeHit) || !BulletOk(bulletComponent.Bullet))) { Fail("bullet"); return false; }
                            return r.Rules.AttenuatorCollisions.ValidateHitBullet(bulletComponent.Bullet, r.Ship);
                        }
                        if (!Serial && bulletData.BeamAttenuator != null) { Fail("attenuator"); return false; }
                        return true;
                    }
                    if (Serial) throw new NotSupportedException(); // BE:1449
                    Fail("user data"); // native throws NotSupportedException
                    return false;
                }
                var hitCrew = (SpaceCrew)userData;
                if (!CrewOk(hitCrew, r.Rules.CrewCollisions)) return false;
                return r.Rules.CrewCollisions.ValidateHitCrew(hitCrew, r.Ship, null, r.ShipTarget);
            }
            if (!ShipOk(hitShip)) { StopShip(hitShip); return false; }
            return IsShipHitValid(hitShip, fixture);
        }

        private bool BulletOk(Bullet? bullet) => bullet?.GetType() == typeof(Bullet) &&
            (bullet.SourceShip == null || (ShipOk(bullet.SourceShip) && (bullet.TargetShip == null || bullet.TargetShip.GetType() == typeof(Ship))));

        private bool IsShipHitValid(Ship hitShip, Fixture hitFixture)
        {
            BeamEmitterRules rules = Rec.Rules;
            if (!rules.FriendlyShipLowCollisions && _IsFixtureLow() && !_CanDamageShip())
            {
                return false;
            }
            if (!rules.FriendlyShipHighCollisions && _IsFixtureHigh() && !_CanDamageShip())
            {
                return false;
            }
            if (!rules.NonFriendlyShipLowCollisions && _IsFixtureLow() && _CanDamageShip())
            {
                return false;
            }
            if (!rules.NonFriendlyShipHighCollisions && _IsFixtureHigh() && _CanDamageShip())
            {
                return false;
            }
            if (_IsStructural() && rules.PenetratesStructure && !hitShip.CanBeDamagedBy(Rec.Ship, Rec.ShipTarget, HealthType.Structural))
            {
                return false;
            }
            return true;
            bool _CanDamageShip() => hitShip.CanBeDamagedBy(Rec.Ship, _GetNonSourceShipTarget(), HealthType.Structural);
            Ship? _GetNonSourceShipTarget()
            {
                Ship? shipTarget = Rec.ShipTarget;
                if (shipTarget == Rec.Ship)
                {
                    return null;
                }
                return shipTarget;
            }
            bool _IsFixtureHigh() => hitFixture.CollisionCategories.HasFlag(Category.Cat5);
            bool _IsFixtureLow() => hitFixture.CollisionCategories.HasFlag(Category.Cat4);
            bool _IsStructural() => hitFixture.CollisionCategories.HasFlag(Category.Cat3);
        }

        private (bool, HitObject) ValidateSecondaryHit(Fixture fixture, V worldPoint)
        {
            Record r = Rec;
            if (fixture.UserData is ArcShield arcShield)
            {
                if (!ShieldOk(arcShield)) { StopShield(arcShield); return (false, default); }
                bool flag = r.Rules.SecondaryFilter.ValidateHitShield(arcShield, r.Ship, null, r.ShipTarget) && arcShield.ValidateShieldHit(worldPoint, (worldPoint - r.Spawn) * 10000f);
                return (flag, flag ? new HitObject(null, null, arcShield, null, null) : default);
            }
            object? userData = fixture.Body?.UserData;
            if (!(userData is SpaceCrew))
            {
                if (userData is Ship hitShip)
                {
                    if (!ShipOk(hitShip)) { StopShip(hitShip); return (false, default); }
                    if (!IsShipHitValid(hitShip, fixture) || !r.Rules.SecondaryFilter.ValidateHitShip(hitShip, r.Ship, r.SourcePlayer, r.ShipTarget) || !_FilterHitPart(hitShip, out Part? hitPart, out BaseCollider? hitCollider))
                    {
                        return (false, default);
                    }
                    return (true, new HitObject(hitPart, hitCollider, null, null, null));
                }
                if (Serial) throw new NotSupportedException(); // BE:1524
                Fail("user data"); // native throws NotSupportedException
                return (false, default);
            }
            var spaceCrew = (SpaceCrew)userData;
            if (!CrewOk(spaceCrew, r.Rules.SecondaryFilter)) return (false, default);
            bool flag2 = r.Rules.SecondaryFilter.ValidateHitCrew(spaceCrew, r.Ship, r.SourcePlayer, r.ShipTarget);
            return (flag2, flag2 ? new HitObject(null, null, null, spaceCrew, null) : default);
            bool _FilterHitPart(Ship ship, [NotNullWhen(true)] out Part? reference, out BaseCollider? hitCollider2)
            {
                reference = ship.Physics.GetContactPart(fixture, worldPoint, out hitCollider2);
                return r.Rules.SecondaryFilter.ValidateHitPart(reference);
            }
        }

        // --- BeamEmitter.cs:1379-1421 -----------------------------------------------------------------------------

        private (Fixture? Fixture, V WorldPoint, V WorldNormal) DoOperationalRaycast(V worldOrigin, V endPoint, float radius)
        {
            Category category = Category.Cat2 | Category.Cat9;
            if (Rec.Rules.CrewCollisions.AffectsCrew)
            {
                category |= Category.Cat10;
            }
            if (Rec.Rules.AttenuationLimit != null)
            {
                category |= Category.Cat12;
            }
            Cast(worldOrigin, endPoint, category, radius, data: false);
            return (_hitFixture, _hitPoint, _hitNormal);
        }

        private (Fixture? Fixture, V WorldPoint, V WorldNormal) DoStructuralRaycast(V worldOrigin, V endPoint, float radius)
        {
            Cast(worldOrigin, endPoint, Category.Cat3, radius, data: false);
            return (_hitFixture, _hitPoint, _hitNormal);
        }

        private ((Fixture? Fixture, V WorldPoint, V WorldNormal) HitResult, HitObject HitObject) DoSecondaryRaycast(V worldOrigin, V endPoint, float radius)
        {
            EffectFilter filter = Rec.Rules.SecondaryFilter;
            Category category = Category.None;
            if (filter.OperationalHealth)
            {
                category |= Category.Cat2;
            }
            if (filter.StructuralHealth)
            {
                category |= Category.Cat3;
            }
            if (filter.Shields)
            {
                category |= Category.Cat9;
            }
            if (filter.Crew)
            {
                category |= Category.Cat10;
            }
            Cast(worldOrigin, endPoint, category, radius, data: true);
            return ((_hitFixture, _hitPoint, _hitNormal), _hitData);
        }

        // --- BeamEmitter.cs:1082-1353 (GetHitInfo after line 1081; the overlap test branch runs in Serial mode only) ---

        internal Outcome Query(out HitInfo info)
        {
            Record r = Rec;
            BeamEmitterRules rules = r.Rules;
            V worldSpawnLoc = r.Spawn;
            Direction worldSpawnDir = r.Dir;
            float length = r.IsInit ? r.CurrentRange : r.LengthField;
            V spawnDirVector = r.SpawnDirVector;
            V worldOrigin = r.WorldOrigin;
            info = new HitInfo { Length = r.InfoLength, EndPoint = r.EndPoint };
            if (rules.ColliderOverlapTest)
            {
                // BE:1082-1095 (BeamFrozen serial path only: TestPointFirst uses PhysicsExtensions' static scratch).
                if (!Serial) { Fail("collider overlap test"); return Outcome.Native; }
                Category pointCategory = Category.Cat2 | Category.Cat9;
                if (rules.CrewCollisions.AffectsCrew) pointCategory |= Category.Cat10;
                if (rules.AttenuationLimit != null) pointCategory |= Category.Cat12;
                Fixture? pointHit = r.World.TestPointFirst(worldSpawnLoc, pointCategory, _pointTest);
                if (pointHit != null)
                {
                    info.Operational.WorldPoint = worldSpawnLoc;
                    info.Operational.WorldNormal = -spawnDirVector;
                    info.Length = 0f;
                    info.EndPoint = worldSpawnLoc;
                    if (_SetOperationalAttenuationInfo(pointHit)) return Outcome.Attenuates;
                    info.Operational.Hit = _GetOperationalHitObject(0f, worldSpawnLoc, pointHit);
                    return Outcome.Ready;
                }
            }
            float radius2 = rules.Width / 2f;
            (Fixture? Fixture, V WorldPoint, V WorldNormal) operationalRaycast = DoOperationalRaycast(worldOrigin, info.EndPoint, radius2);
            if (Abort != null) return Outcome.Native;
            if (_WasEmitterBlocked())
            {
                operationalRaycast.WorldPoint = worldSpawnLoc;
                return _SetOperationalInfo(ref info, 0f) ? Outcome.Attenuates : Abort != null ? Outcome.Native : Outcome.Ready;
            }
            float operationalDistance = operationalRaycast.Fixture != null ? worldSpawnLoc.DistanceTo(operationalRaycast.WorldPoint) : float.PositiveInfinity;
            if (operationalRaycast.Fixture != null)
            {
                info.EndPoint = operationalRaycast.WorldPoint;
                info.Length = operationalDistance;
            }
            (Fixture? Fixture, V WorldPoint, V WorldNormal) structuralRaycast = DoStructuralRaycast(worldSpawnLoc, info.EndPoint, radius2);
            if (Abort != null) return Outcome.Native;
            float structuralDistance = structuralRaycast.Fixture != null ? worldSpawnLoc.DistanceTo(structuralRaycast.WorldPoint) : float.PositiveInfinity;
            bool secondary = _TryGetSecondaryInfo(ref info);
            if (Abort != null) return Outcome.Native;
            if (secondary)
            {
                return Outcome.Ready;
            }
            if (structuralRaycast.Fixture != null)
            {
                _SetStructuralInfo(ref info);
                if (Abort != null) return Outcome.Native;
                if (!rules.PenetratesStructure)
                {
                    _OverwriteInfoWithStructuralHit(ref info);
                    return Outcome.Ready;
                }
                _CollectAllStructuralHits(ref info);
            }
            if (operationalRaycast.Fixture != null)
            {
                if (_SetOperationalInfo(ref info, operationalDistance)) return Outcome.Attenuates;
                if (Abort != null) return Outcome.Native;
            }
            if (rules.KeepLengthWhenHitNothing && _HitNothing(ref info))
            {
                info.Length = length;
                info.EndPoint = worldSpawnLoc + V.FromAngle(worldSpawnDir) * info.Length;
            }
            return Outcome.Ready;

            void _CollectAllStructuralHits(ref HitInfo info)
            {
                info.AllStructuralHits = TempList<StructuralHitInfo>.Alloc();
                if (!(structuralRaycast.Fixture?.Body?.UserData is Ship ship))
                {
                    return;
                }
                Segment segment = new Segment(ship.DetTransformPointFromWorld(structuralRaycast.WorldPoint), ship.DetTransformPointFromWorld(info.EndPoint));
                using TempList<IntVector2> tempList = TempList<IntVector2>.Alloc();
                segment.Raytrace(tempList);
                foreach (IntVector2 item in tempList)
                {
                    IntVector2 v = item;
                    if (info.AllStructuralHits.Count >= r.MaxStructuralPenetrations)
                    {
                        break;
                    }
                    Part? part = ship.Parts[v];
                    if (part != null && part.Rules.HealthType == HealthType.Structural)
                    {
                        V cell = v; // native: (Vector2)v
                        V point = segment.FindClosestPoint(cell + V.Half);
                        V point2 = ship.DetTransformPointToWorld(point);
                        float dist = worldSpawnLoc.DistanceTo(in point2);
                        V worldNormal = (worldSpawnLoc - point2).Normalize();
                        if (_ValidateHitObject(ship, dist))
                        {
                            info.AllStructuralHits.Add(new StructuralHitInfo(part, null, point2, worldNormal));
                        }
                    }
                }
            }
            HitObject _GetOperationalHitObject(float distance, V hitWorldPoint, Fixture? fixture2)
            {
                if (fixture2 == null)
                {
                    return default;
                }
                if (fixture2.UserData is SpaceCrew spaceCrew)
                {
                    // _ValidateHitObject reads SimNode.Sim and SimObject.UniqueID only (non-virtual).
                    if (!Serial && RuntimeType(spaceCrew) != typeof(SpaceCrew)) { Fail("crew type"); return default; }
                    if (_ValidateHitObject(spaceCrew, distance)) return new HitObject(null, null, null, spaceCrew, null);
                }
                if (!(fixture2.Body?.UserData is Ship ship) || !_ValidateHitObject(ship, distance))
                {
                    return default;
                }
                if (fixture2.UserData is ArcShield { IsOperational: not false } arcShield)
                {
                    return new HitObject(null, null, arcShield, null, null);
                }
                Part? contactPart = ship.Physics.GetContactPart(fixture2, hitWorldPoint, out BaseCollider? hitCollider);
                return new HitObject(contactPart, hitCollider, null, null, null);
            }
            bool _HitNothing(ref HitInfo info)
            {
                if (!info.Operational.Hit.HasObject())
                {
                    return info.Structural.Part == null;
                }
                return false;
            }
            bool _IsSecondaryOverridden()
            {
                EffectFilter? secondaryOverrideFilter = rules.SecondaryOverrideFilter;
                if (secondaryOverrideFilter == null)
                {
                    return false;
                }
                HitObject hitObject = _GetOperationalHitObject(operationalDistance, operationalRaycast.WorldPoint, operationalRaycast.Fixture);
                if (Abort != null) return false;
                if (hitObject.Part != null)
                {
                    if (hitObject.Part.Ship != null && !ShipOk(hitObject.Part.Ship)) { StopShip(hitObject.Part.Ship); return false; }
                    return secondaryOverrideFilter.ValidateHitShipAndPart(hitObject.Part.Ship, hitObject.Part, r.Ship, r.ShipPlayer, r.ShipTarget);
                }
                if (hitObject.Shield != null)
                {
                    if (!ShieldOk(hitObject.Shield)) { StopShield(hitObject.Shield); return false; }
                    return secondaryOverrideFilter.ValidateHitShield(hitObject.Shield, r.Ship, r.ShipPlayer, r.ShipTarget);
                }
                if (hitObject.Crew != null)
                {
                    if (!CrewOk(hitObject.Crew, secondaryOverrideFilter)) return false;
                    return secondaryOverrideFilter.ValidateHitCrew(hitObject.Crew, r.Ship, r.ShipPlayer, r.ShipTarget);
                }
                return false;
            }
            void _OverwriteInfoWithStructuralHit(ref HitInfo info)
            {
                info.Operational = default;
                info.EndPoint = structuralRaycast.WorldPoint;
                info.Length = structuralDistance;
            }
            // True: the native code would attenuate a bullet here (line 1235); the record is never replayed.
            // Frozen: the attenuation (attenuator, Segment(spawn, initialBeamEnd)) is recorded and the query continues as native;
            // the attenuator HitObject set at BE:1236 is overwritten by the caller exactly as at BE:1092/1250.
            bool _SetOperationalAttenuationInfo(Fixture fixture2)
            {
                if (rules.AttenuationLimit == null || fixture2.Body?.UserData is not BulletData bulletData || bulletData.BeamAttenuator == null) return false;
                if (!Frozen) return true;
                r.Attenuator = bulletData.BeamAttenuator;
                r.AttenuationSegment = new Segment(in worldSpawnLoc, in r.EndPoint);
                return false;
            }
            bool _SetOperationalInfo(ref HitInfo info, float distance)
            {
                Fixture? fixture2 = operationalRaycast.Fixture;
                if (fixture2 != null)
                {
                    info.Operational.WorldPoint = operationalRaycast.WorldPoint;
                    info.Operational.WorldNormal = operationalRaycast.WorldNormal;
                    info.Length = distance;
                    info.EndPoint = operationalRaycast.WorldPoint;
                    if (_SetOperationalAttenuationInfo(fixture2)) return true;
                    info.Operational.Hit = _GetOperationalHitObject(distance, operationalRaycast.WorldPoint, fixture2);
                }
                return false;
            }
            void _SetStructuralInfo(ref HitInfo info)
            {
                Fixture? fixture2 = structuralRaycast.Fixture;
                if (fixture2 != null)
                {
                    StructuralHitInfo? structuralHitInfo = _TryGetStructuralHitInfo(fixture2, structuralRaycast.WorldPoint, structuralRaycast.WorldNormal, structuralDistance);
                    if (structuralHitInfo.HasValue)
                    {
                        info.Structural = structuralHitInfo.Value;
                    }
                }
            }
            bool _TryGetSecondaryInfo(ref HitInfo info)
            {
                float? num = r.SecondaryWidth;
                ((Fixture? Fixture, V WorldPoint, V WorldNormal) HitResult, HitObject HitObject) raycastHitInfo;
                float num2;
                if (num.HasValue)
                {
                    float valueOrDefault = num.GetValueOrDefault();
                    if (!(valueOrDefault <= 0f))
                    {
                        bool overridden = _IsSecondaryOverridden();
                        if (Abort != null || overridden)
                        {
                            return false;
                        }
                        raycastHitInfo = DoSecondaryRaycast(worldSpawnLoc, info.EndPoint, valueOrDefault / 2f);
                        if (Abort != null || raycastHitInfo.HitResult.Fixture == null)
                        {
                            return false;
                        }
                        num2 = worldSpawnLoc.DistanceTo(raycastHitInfo.HitResult.WorldPoint);
                        if (num2 > operationalDistance)
                        {
                            return false;
                        }
                        if (structuralDistance < num2)
                        {
                            _SetStructuralInfo(ref info);
                            if (rules.PenetratesStructure)
                            {
                                Part? part = raycastHitInfo.HitObject.Part;
                                if (part == null || part.Rules.HealthType != HealthType.Structural)
                                {
                                    goto IL_0102;
                                }
                            }
                            _OverwriteInfoWithStructuralHit(ref info);
                            return true;
                        }
                        goto IL_0102;
                    }
                }
                return false;
            IL_0102:
                info.Operational.WorldPoint = raycastHitInfo.HitResult.WorldPoint;
                info.Operational.WorldNormal = raycastHitInfo.HitResult.WorldNormal;
                info.Length = num2;
                info.EndPoint = raycastHitInfo.HitResult.WorldPoint;
                info.Operational.Hit = raycastHitInfo.HitObject;
                return true;
            }
            StructuralHitInfo? _TryGetStructuralHitInfo(Fixture fixture2, V hitPoint, V hitNormal, float hitDistance)
            {
                if (!(fixture2.Body?.UserData is Ship ship))
                {
                    return null;
                }
                if (!_ValidateHitObject(ship, hitDistance))
                {
                    return null;
                }
                Part? contactPart = ship.Physics.GetContactPart(fixture2, hitPoint, out BaseCollider? hitCollider);
                if (contactPart == null)
                {
                    return null;
                }
                return new StructuralHitInfo(contactPart, hitCollider, hitPoint, hitNormal);
            }
            bool _ValidateHitObject(SimObject obj, float dist)
            {
                if (obj.Sim == r.Sim)
                {
                    if (!(r.InitHitObjectId == obj.UniqueID))
                    {
                        return dist <= r.RangeValue;
                    }
                    return true;
                }
                return false;
            }
            bool _WasEmitterBlocked()
            {
                if (rules.AntiPhasingRaycastDistance <= 0f || operationalRaycast.Fixture == null)
                {
                    return false;
                }
                V v = operationalRaycast.WorldPoint - worldSpawnLoc;
                return spawnDirVector.Dot(in v) < 0f;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // Instrumentation (main thread only)

    internal static class Stats
    {
        internal static long LostToFallback;
        internal static long Verified, VerifyMismatches, VerifiedShield, VerifiedPenetrating, VerifiedSecondary;
        internal static long Ticks, Records, Eligible, Accepted, Missed, Wasted, AcceptedQueryTicks, WastedQueryTicks, MissedQueryTicks, SessionTicks, SnapshotTicks;
        internal static readonly Dictionary<string, long> Reasons = new(StringComparer.Ordinal);
        private static long _lastLog = Stopwatch.GetTimestamp();
        internal static void Fallback(string reason)
        {
            long n = Reasons[reason] = Reasons.GetValueOrDefault(reason) + 1;
            if (n == 1 && reason.StartsWith("session:", StringComparison.Ordinal)) Performance.Log.Info("Beam parallel no session: " + reason);
        }
        private static readonly Dictionary<(string, string), string> Keys = new();
        /// <summary>Allocation-free after the first occurrence of each prefix/reason pair.</summary>
        internal static void Fallback(string prefix, string reason)
        {
            if (!Keys.TryGetValue((prefix, reason), out string? key)) Keys[(prefix, reason)] = key = prefix + reason;
            Fallback(key);
        }
        internal static void Begin(Session s, long sessionTicks, long snapshotTicks)
        {
            Ticks++; Records += s.Records.Count; Eligible += s.Work.Count; SessionTicks += sessionTicks; SnapshotTicks += snapshotTicks;
        }
        internal static void Accept(Record r) { Accepted++; AcceptedQueryTicks += r.QueryTicks; }
        internal static void Miss(Record r, string reason) { Missed++; MissedQueryTicks += r.QueryTicks; Fallback("replay native: ", reason); }
        internal static void Waste(Record r) { Wasted++; WastedQueryTicks += r.QueryTicks; }
        internal static void OnEnd() { if (Ticks == 1 || Ticks % 500 == 0) Log(Ticks == 1 ? "first session" : "periodic"); }
        internal static void MaybeLog()
        {
            if ((Ticks == 0 && Reasons.Count == 0) || Stopwatch.GetElapsedTime(_lastLog).TotalSeconds < (Ticks < 100 ? 5 : 30)) return;
            Log("periodic");
        }
        internal static void Log(string label)
        {
            _lastLog = Stopwatch.GetTimestamp();
            if (Ticks == 0) { Performance.Log.Info("Beam parallel " + label + ": no sessions; reasons " + string.Join("; ", Reasons.Select(kv => kv.Key + "=" + kv.Value))); return; }
            double ms = 1000.0 / Stopwatch.Frequency, n = Ticks;
            Performance.Log.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Beam parallel {label}: verify {Verify} verified {Verified} (shield {VerifiedShield} penetrating {VerifiedPenetrating} secondary {VerifiedSecondary}) mismatches {VerifyMismatches} ticks {Ticks} records/tick {Records / n:F1} eligible/tick {Eligible / n:F1} accepted/tick {Accepted / n:F1} missed/tick {Missed / n:F2} wasted/tick {Wasted / n:F2} lost-to-fallback/tick {LostToFallback / n:F2} | " +
                $"query ms/tick accepted {AcceptedQueryTicks * ms / n:F3} missed {MissedQueryTicks * ms / n:F3} wasted {WastedQueryTicks * ms / n:F3} | pre-bucket wall ms/tick {SessionTicks * ms / n:F3} (snapshot {SnapshotTicks * ms / n:F3}) | reasons ") +
                string.Join("; ", Reasons.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + "=" + kv.Value)));
        }
    }
}
