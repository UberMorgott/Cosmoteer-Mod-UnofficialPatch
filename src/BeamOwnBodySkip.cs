using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Parts.Defenses;
using Cosmoteer.Ships.Parts.Weapons;
using Cosmoteer.Simulation;
using Halfling;
using Halfling.Application;
using Halfling.Physics2D.Collision;
using Halfling.Physics2D.Dynamics;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>
/// A beam's three hit raycasts (operational, structural, secondary: BeamEmitter.GetHitInfo) start on the emitter's own ship,
/// so the broad phase hands the ship's own body to <c>World.RayCastCallbackBroadPhaseWrapper</c>, which walks the body's
/// fixture tree and shape-tests every own fixture in the (often 16-wide) band. In combat that is ~77% of beam raycast time
/// (fleet-combat x8), and every own fixture is then rejected by the beam's hit validator.
/// <para/>
/// When every own fixture would be rejected, the walk can only return the input MaxFraction (no clip, no callback side
/// effect), so the wrapper returns exactly that without walking. Rejection is decided per ray, for the fixtures its category filter reaches, with the real
/// native predicates, not a copy: the validators reach an own fixture either through its UserData ArcShield (filter
/// ValidateHitShield of the beam's ShieldCollisions, or SecondaryFilter for the secondary ray) or through the ship body,
/// where IsShipHitValid(ownShip, fixture) reads only fixture.CollisionCategories (Cat3/Cat4/Cat5) and its false rejects
/// both validators; ValidateSecondaryHit also rejects every own body fixture when SecondaryFilter.ValidateHitShip(own, own)
/// fails (fixture-independent; ion beams collide with own parts but their secondary filter excludes the own ship). So each own shield and each Cat3/4/5 combination present in the body (one detached dummy fixture per
/// combination) whose categories meet the ray filter is evaluated (results reused within one GetHitInfo); any acceptance keeps the native walk.
/// <para/>
/// The predicates call GetShipTarget and the mode's CanDamage, so the decision is admitted only where BeamParallel's audit
/// shows them pure (audited mode pair, static beam shape, vetted target graph with no lazy refresh pending): only the number
/// of these pure calls changes. Census per body is cached on FixtureList version and a ship-fixture category epoch
/// (UserData is set before a fixture is added). The skip applies only to the armed beam raycast itself (World.RayCast
/// depth), never to nested queries. Off (native) on foreign Harmony patches, changed game files or with the beam target
/// memo prototype. Off: <c>UNOFFICIAL_PATCH_BEAM_OWN_SKIP=0</c>.
/// </summary>
internal static class BeamOwnBodySkip
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("UNOFFICIAL_PATCH_BEAM_OWN_SKIP"));
    internal static bool IsRequested(string? value) => value != "0";

    private static MethodInfo M(Type t, string name) => AccessTools.Method(t, name) ?? throw new MissingMethodException(t.Name, name);

    internal static (MethodBase Target, Type Patch)[] Targets =>
    [
        (M(typeof(BeamEmitter), "GetHitInfo"), typeof(HitScope)),
        (M(typeof(BeamEmitter), "DoOperationalRaycast"), typeof(PrimaryRay)),
        (M(typeof(BeamEmitter), "DoStructuralRaycast"), typeof(PrimaryRay)),
        (M(typeof(BeamEmitter), "DoSecondaryRaycast"), typeof(SecondaryRay)),
        (AccessTools.Method(typeof(World), nameof(World.RayCast), [typeof(Func<Fixture, System.Numerics.Vector2, System.Numerics.Vector2, float, float>),
            typeof(System.Numerics.Vector2), typeof(System.Numerics.Vector2), typeof(Category), typeof(float)]) ?? throw new MissingMethodException(nameof(World), "RayCast"), typeof(RayDepth)),
        (M(typeof(World), "RayCastCallbackBroadPhaseWrapper"), typeof(BodyWalk)),
        (AccessTools.PropertySetter(typeof(Fixture), nameof(Fixture.CollisionCategories)), typeof(CategoryEpoch)),
    ];

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? Targets : [];

    private static int _ready;
    private static bool _attempted;
    private static Director? _director;
    internal static bool Ready => Volatile.Read(ref _ready) != 0;

    // ---- Per-thread scope ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Admitted GetHitInfo: own body, its census and lazily evaluated predicate results (bit per Cat3/4/5 mask: evaluated /
    /// accepted; shield results per ray kind). Default (World null) = not admitted.
    /// </summary>
    internal struct Scope
    {
        public World? World;
        public Body? Body;
        public BeamEmitter? Beam;
        public Census? Census;
        public int Known, Accept, ShieldKnown, ShieldAccept; // shield bits: 1 primary, 2 secondary; 4: secondary ship filter evaluated / rejects own ship; 4: secondary ship filter evaluated / rejects own ship
    }

    [ThreadStatic] private static Scope _scope;
    [ThreadStatic] private static int _rayDepth;   // open World.RayCast calls
    [ThreadStatic] private static int _armedDepth; // World.RayCast depth of the armed beam ray, 0 = none
    [ThreadStatic] private static int _armedKind;  // 1 primary, 2 secondary
    [ThreadStatic] private static bool _skipOwn;   // the armed ray's own-body walk would reject every fixture

    internal static long Decisions, Skippable, Skips, ShieldAccepts; // stats (main thread)
    private static readonly HashSet<Category> Accepted = []; // own fixture categories a beam validator accepted (stats)

    public static class HitScope
    {
        public static void Prefix(BeamEmitter __instance, out Scope __state)
        {
            __state = _scope;
            _scope = Ready ? Admit(__instance) : default;
        }

        public static void Finalizer(Scope __state) => _scope = __state;
    }

    public static class PrimaryRay
    {
        public static void Prefix(out (int, int, bool) __state) => Arm(1, out __state);
        public static void Finalizer((int, int, bool) __state) => (_armedDepth, _armedKind, _skipOwn) = __state;
    }

    public static class SecondaryRay
    {
        public static void Prefix(out (int, int, bool) __state) => Arm(2, out __state);
        public static void Finalizer((int, int, bool) __state) => (_armedDepth, _armedKind, _skipOwn) = __state;
    }

    private static void Arm(int kind, out (int, int, bool) saved)
    {
        saved = (_armedDepth, _armedKind, _skipOwn);
        // The RayCastFirst inside is the next World.RayCast; it decides with its own category filter.
        (_armedDepth, _armedKind, _skipOwn) = _scope.World != null ? (_rayDepth + 1, kind, false) : (0, 0, false);
    }

    public static class RayDepth
    {
        public static void Prefix(World __instance, Category categories)
        {
            if (++_rayDepth == _armedDepth && _armedDepth != 0)
                _skipOwn = ReferenceEquals(__instance, _scope.World) && OwnRejects(ref _scope, _armedKind == 2, categories);
        }

        public static void Finalizer() => _rayDepth--;
    }

    public static class BodyWalk
    {
        public static bool Prefix(World __instance, RayCastInput rayCastInput, int proxyId, ref float __result)
        {
            if (!_skipOwn || _armedDepth != _rayDepth || !ReferenceEquals(__instance, _scope.World)) return true;
            BodyFixtureProxy proxy = __instance.ContactManager.BroadPhase.GetProxy(proxyId);
            if (proxy.Fixture != null || !ReferenceEquals(proxy.Body, _scope.Body)) return true;
            Skips++;
            __result = rayCastInput.MaxFraction; // what the walk returns when every own callback rejects
            return false;
        }
    }

    /// <summary>A ship-body fixture's categories changed: every census is rebuilt (rare; bullets are not ship bodies).</summary>
    public static class CategoryEpoch
    {
        public static void Postfix(Fixture __instance)
        {
            if (__instance.Body?.UserData is Ship) Interlocked.Increment(ref _epoch);
        }
    }

    // ---- Decision ------------------------------------------------------------------------------------------------------

    private static long _epoch;

    /// <summary>The distinct categories of the body's non-shield fixtures, and its shields (fixture UserData ArcShield).</summary>
    internal sealed class Census
    {
        public int Version = -1;
        public long Epoch = -1;
        public readonly List<Category> Values = [];
        public readonly List<ArcShield> Shields = [];
        public Category ShieldCategories;
    }

    private static readonly ConditionalWeakTable<Body, Census> Censuses = new();
    private static readonly AccessTools.FieldRef<List<Fixture>, int> ListVersion = AccessTools.FieldRefAccess<List<Fixture>, int>("_version");

    /// <summary>Detached fixtures carrying only a Cat3/Cat4/Cat5 combination, the one thing IsShipHitValid reads from a fixture.</summary>
    private static readonly Fixture[] Dummies = CreateDummies();

    private static Fixture[] CreateDummies()
    {
        var dummies = new Fixture[8];
        for (int m = 0; m < 8; m++) dummies[m] = new Fixture { _collisionCategories = MaskCategories(m) };
        return dummies;
    }

    internal static Category MaskCategories(int m) =>
        ((m & 1) != 0 ? Category.Cat3 : Category.None) | ((m & 2) != 0 ? Category.Cat4 : Category.None) | ((m & 4) != 0 ? Category.Cat5 : Category.None);

    internal static int Mask(Category c) =>
        ((c & Category.Cat3) != Category.None ? 1 : 0) | ((c & Category.Cat4) != Category.None ? 2 : 0) | ((c & Category.Cat5) != Category.None ? 4 : 0);

    internal static Census Take(Body body)
    {
        Census c = Censuses.GetValue(body, static _ => new Census());
        List<Fixture> list = body.FixtureList;
        int version = ListVersion(list);
        long epoch = Volatile.Read(ref _epoch);
        if (c.Version == version && c.Epoch == epoch) return c;
        c.Values.Clear();
        c.Shields.Clear();
        c.ShieldCategories = Category.None;
        foreach (Fixture f in list)
        {
            if (f.UserData is ArcShield shield)
            {
                c.Shields.Add(shield);
                c.ShieldCategories |= f.CollisionCategories;
            }
            else if (!c.Values.Contains(f.CollisionCategories)) c.Values.Add(f.CollisionCategories);
        }
        c.Version = version;
        c.Epoch = epoch;
        return c;
    }

    /// <summary>Admission (audited, pure predicates) and the census; the predicates run per ray in <see cref="OwnRejects"/>.</summary>
    internal static Scope Admit(BeamEmitter beam)
    {
        try
        {
            if (beam.GetType() != typeof(BeamEmitter) || beam.Sim is not { } sim || beam.Ship is not { } ship) return default;
            if (!ReferenceEquals(sim.SceneThread, Thread.CurrentThread)) return default;
            if (!BeamParallel.AuditedMode(sim) || !BeamParallel.StaticEligible(beam, out _) || !BeamParallel.QueryGraphVetted(beam, sim, out _)) return default;
            if (ship.Physics.Body is not { } body || !ReferenceEquals(body.UserData, ship) || !body.UseWholeBodyBroadPhase ||
                !ReferenceEquals(body.World, sim.PhysicsWorld)) return default;
            Decisions++;
            return new Scope { World = sim.PhysicsWorld, Body = body, Beam = beam, Census = Take(body) };
        }
        catch (Exception ex)
        {
            Reject("admission: " + ex.Message);
            return default;
        }
    }

    /// <summary>
    /// True when the walk of the own body with this category filter can only reject: the fixture tree visits only fixtures
    /// whose categories intersect the filter, so only those classes are evaluated (ValidateHit / ValidateSecondaryHit order:
    /// UserData ArcShield first, then the ship body through IsShipHitValid).
    /// </summary>
    internal static bool OwnRejects(ref Scope s, bool secondary, Category categories)
    {
        try
        {
            Census census = s.Census!;
            BeamEmitter beam = s.Beam!;
            Ship ship = beam.Ship!;
            // ValidateSecondaryHit rejects every own non-shield fixture when its fixture-independent _FilterHitShip fails.
            if (secondary && (s.ShieldKnown & 4) == 0)
            {
                s.ShieldKnown |= 4;
                int sourcePlayer = beam.Sim!.CurrentActionSourcePlayer?.PlayerId ?? ship.Metadata.PlayerIndex;
                if (!beam.Rules.SecondaryFilter.ValidateHitShip(ship, ship, sourcePlayer, beam.GetShipTarget(checkProhibited: true))) s.ShieldAccept |= 4;
            }
            bool shipFiltered = secondary && (s.ShieldAccept & 4) != 0;
            foreach (Category value in census.Values)
            {
                if ((value & categories) == Category.None || shipFiltered) continue;
                int bit = 1 << Mask(value);
                if ((s.Known & bit) == 0)
                {
                    s.Known |= bit;
                    if (beam.IsShipHitValid(ship, Dummies[Mask(value)])) { s.Accept |= bit; Accepted.Add(value); }
                }
                if ((s.Accept & bit) != 0) return false;
            }
            if ((census.ShieldCategories & categories) != Category.None)
            {
                int bit = secondary ? 2 : 1;
                if ((s.ShieldKnown & bit) == 0)
                {
                    s.ShieldKnown |= bit;
                    Ship? target = beam.GetShipTarget(checkProhibited: true);
                    EffectFilter filter = secondary ? beam.Rules.SecondaryFilter : beam.Rules.ShieldCollisions;
                    foreach (ArcShield shield in census.Shields)
                        if (filter.ValidateHitShield(shield, ship, null, target)) { s.ShieldAccept |= bit; ShieldAccepts++; break; }
                }
                if ((s.ShieldAccept & bit) != 0) return false;
            }
            Skippable++;
            return true;
        }
        catch (Exception ex)
        {
            Reject("decision: " + ex.Message);
            return false;
        }
    }
    // ---- Startup and guards ----------------------------------------------------------------------------------------------

    internal static bool Initialize()
    {
        if (!Requested) return true;
        if (_attempted) return Ready;
        _attempted = true;
        try
        {
            if (BeamTargetMemo.Requested) throw new NotSupportedException("beam target memo prototype requested (counts native target calls)");
            Director director = App.Director ?? throw new NotSupportedException("requires the native Director");
            BeamParallel.ValidateNative(); // file + IL pins of every predicate and target getter the decision calls
            foreach ((MethodBase target, Type patch) in Targets)
            {
                Patches? info = Harmony.GetPatchInfo(target);
                if (info == null || (!Has(info.Prefixes, patch) && !Has(info.Postfixes, patch)))
                    throw new NotSupportedException("hook missing on " + target.DeclaringType?.Name + "." + target.Name);
                if (!Patcher.TryDisableInlining(target, out string? error)) throw new NotSupportedException(error);
            }
            if (!BeamFrozen.Compatible(out string? compatibility)) throw new NotSupportedException(compatibility);
            _director = director;
            director.FrameStarting += OnFrameStarting;
            Volatile.Write(ref _ready, 1);
            Log.Info("Beam own-body skip ready");
            return true;
        }
        catch (Exception ex)
        {
            Reject(ex.Message);
            return false;
        }
    }

    private static bool Has(IEnumerable<Patch> list, Type patch)
    {
        foreach (Patch p in list) if (p.owner == Patcher.HarmonyId && p.PatchMethod.DeclaringType == patch) return true;
        return false;
    }

    private static readonly BeamParallel.HarmonySnapshot Snapshot = new();
    private static long _frames;

    private static void OnFrameStarting(object? sender, EventArgs e)
    {
        if (!Snapshot.Unchanged())
        {
            // Remembered before the full check: a patch landing during it fails the next comparison and re-runs it.
            Snapshot.Remember();
            if (!BeamFrozen.Compatible(out string? error)) { Snapshot.Reset(); Reject(error ?? "compatibility unavailable"); return; }
        }
        if (++_frames % 600 == 0) Log.Info($"Beam own-body skip: {Decisions} admitted hit queries, {Skippable} rays skippable, {Skips} body walks skipped; own shields accepted {ShieldAccepts}x, own categories accepted: [{string.Join(", ", Accepted)}]");
    }

    private static void Reject(string reason)
    {
        Disable();
        Log.Info("Beam own-body skip disabled: " + reason);
    }

    /// <summary>Test seam: decisions run without the startup guards (the test installs the hooks itself).</summary>
    internal static void SetReadyForTests(bool ready) => Volatile.Write(ref _ready, ready ? 1 : 0);

    internal static void Disable()
    {
        Volatile.Write(ref _ready, 0);
        if (_director != null) _director.FrameStarting -= OnFrameStarting;
        _director = null;
    }
}
