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

namespace UnofficialPatch.Tests;

[Collection(nameof(BeamParallelTests))]
public sealed class BeamParallelTests
{
    [Fact]
    public void NativeDependenciesArePinned() => BeamParallel.ValidateNative();

    [Fact]
    public void OnlyAuditedExactModePairsAreOwned()
    {
        SimRoot sim = Shell<SimRoot>(); GameRoot game = Shell<GameRoot>(); sim.Game = game;
        bool With(object simMode, object gameMode)
        {
            Set(sim, "<Mode>k__BackingField", simMode); Set(game, "<Mode>k__BackingField", gameMode);
            return BeamParallel.AuditedMode(sim);
        }
        Assert.True(With(Shell<CreativeSimModeManager>(), Shell<CreativeGameModeManager>()));
        Assert.True(With(Shell<Cosmoteer.Modes.Career.CareerSimModeManager>(), Shell<Cosmoteer.Modes.Career.CareerGameModeManager>()));
        Assert.False(With(Shell<Cosmoteer.Modes.Career.CareerSimModeManager>(), Shell<CreativeGameModeManager>())); // mismatched pair
        Assert.False(With(Shell<Cosmoteer.Modes.SimModeManager>(), Shell<Cosmoteer.Modes.GameModeManager>())); // base, unaudited
    }

    [Fact]
    public void CompatibilityCacheSeesEveryHarmonyChange()
    {
        var harmony = new Harmony("UnofficialPatch.Tests.BeamParallel.State");
        try
        {
            var snapshot = new BeamParallel.HarmonySnapshot();
            snapshot.Remember();
            Assert.True(snapshot.Unchanged());
            MethodInfo target = AccessTools.Method(typeof(BeamParallelTests), nameof(PatchTarget));
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(BeamParallelTests), nameof(PatchPrefix)));
            Assert.False(snapshot.Unchanged());
            snapshot.Remember();
            Assert.True(snapshot.Unchanged());
            harmony.Unpatch(target, HarmonyPatchType.Prefix, harmony.Id); // same key, replaced patch-info array
            Assert.False(snapshot.Unchanged());
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static int PatchTarget() => 1;
    private static void PatchPrefix() { }

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
            Assert.Null(BeamParallel.Difference(native, record.Info));
            Assert.True(f.RandAfterNative == f.RandState(), "preparation must consume exactly the native RNG draws");
            {
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

    /// <summary>A derived crew type is unaudited: the worker stops at it (that beam then runs on the serial path).</summary>
    [Fact]
    public void DerivedCrewTypeStopsTheWorker()
    {
        using var f = new Setup("crew-type", beams: 2);
        using var session = f.Speculate(2);
        BeamParallel.Record first = session.Session.Records[0], second = session.Session.Records[1];
        Assert.Equal(BeamParallel.Outcome.Native, first.Outcome); Assert.Equal("crew type", first.Reason);
        Assert.Null(first.Info.AllStructuralHits);
        Assert.Equal(BeamParallel.Outcome.Ready, second.Outcome);
    }

    /// <summary>An unaudited target getter is refused by the graph check and never invoked.</summary>
    [Fact]
    public void UnknownTargetGetterIsNeverInvoked()
    {
        RecordingWeapon.Calls = 0;
        using var f = new Setup("operational");
        f.Beams[0]._getTargetFrom = Shell<RecordingWeapon>();
        Assert.False(BeamParallel.QueryGraphVetted(f.Beams[0], f.Sim, out string? why));
        Assert.Equal("target graph: provider type", why);
        Assert.Equal(0, RecordingWeapon.Calls);
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
        using (var session = f.Speculate(1, held))
            Assert.All(session.Session.Records, r => Assert.Equal(BeamParallel.Outcome.Ready, r.Outcome));
        Assert.Equal([false], held.ToArray());
    }

    [Theory]
    [InlineData("own", true)]
    [InlineData("own-shield", true)]
    [InlineData("own-accept-parts", true)] // own operational cells accepted: only the structural ray (Cat3, rejected by PenetratesStructure) skips
    [InlineData("own-secondary-self", true)] // own ship passes the secondary filter: only the structural ray skips
    [InlineData("own-accept-shield", true)] // own shield accepted by ShieldCollisions: only the secondary ray skips
    [InlineData("secondary", false)]
    public void OwnBodySkipMatchesNative(string scenario, bool skips)
    {
        var harmony = new Harmony("UnofficialPatch.Tests.BeamOwnBodySkip");
        try
        {
            using var f = new Setup(scenario, beams: 4);
            var rands = new List<(ulong, ulong)>();
            var natives = Enumerable.Range(0, 4).Select(i => { HitInfo n = f.Native(i); rands.Add(f.RandAfterNative); return n; }).ToList();
            foreach ((MethodBase target, Type patch) in BeamOwnBodySkip.Targets)
            {
                HarmonyMethod? Hook(string name) => patch.GetMethod(name, BindingFlags.Public | BindingFlags.Static) is { } m ? new HarmonyMethod(m) : null;
                harmony.Patch(target, Hook("Prefix"), Hook("Postfix"), finalizer: Hook("Finalizer"));
            }
            BeamOwnBodySkip.SetReadyForTests(true);
            long skipsBefore = BeamOwnBodySkip.Skips;
            for (int i = 0; i < 4; i++)
            {
                HitInfo patched = f.Native(i);
                try
                {
                    Assert.Null(BeamParallel.Difference(natives[i], patched));
                    Assert.Equal(rands[i], f.RandAfterNative);
                }
                finally { patched.Dispose(); }
            }
            Assert.Equal(skips, BeamOwnBodySkip.Skips > skipsBefore);
            if (scenario.StartsWith("own", StringComparison.Ordinal) && scenario is not ("own-accept-parts" or "own-secondary-self"))
                Assert.True(natives.Any(n => n.Operational.Hit.Part != null || n.Structural.Part != null || n.Operational.Hit.Shield != null), "beams must reach a hit");
            if (scenario is "own-accept-parts" or "own-secondary-self") Assert.Contains(natives, n => n.Length < 50f); // clipped by an own cell: the walk that must not be skipped
            natives.ForEach(n => n.Dispose());
        }
        finally
        {
            BeamOwnBodySkip.SetReadyForTests(false);
            harmony.UnpatchAll(harmony.Id);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "ClassCannotBeInstantiated", Justification = "Only uninitialized shells are created.")]
    private sealed class RecordingWeapon : FixedWeapon
    {
        internal static int Calls;
        private RecordingWeapon() : base(null!) { }
        public override Ship? GetShipTarget(bool checkRange, bool checkProhibited) { Calls++; return null; }
        public override ITarget? GetFireTarget(bool checkRange, bool checkArc, bool checkProhibited, bool includeExplicitTarget) { Calls++; return null; }
    }
    /// <summary>An unaudited crew type (never constructed; shells only).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "ClassCannotBeInstantiated", Justification = "Only uninitialized shells are created.")]
    private sealed class ForeignCrew : SpaceCrew
    {
        private ForeignCrew() : base(null!, default, default) { }
    }

    // --- Harness -------------------------------------------------------------------------------------------------------


    internal sealed class Speculation(BeamFrozen.Session session) : IDisposable
    {
        internal BeamFrozen.Session Session { get; } = session;
        public void Dispose() => BeamFrozen.End(Session);
    }

    private sealed class Setup : IDisposable
    {
        internal readonly SimRoot Sim = Shell<SimRoot>();
        internal readonly GameRoot Game = Shell<GameRoot>();
        internal readonly World World = new(new DynamicTreeBroadPhase<BodyFixtureProxy>());
        internal readonly Ship Source;
        internal readonly WeaponManager Manager = Shell<WeaponManager>();
        internal readonly List<BeamEmitter> Beams = [];
        private readonly string _scenario;
        internal (ulong, ulong) RandAfterNative;

        internal Setup(string scenario, int beams = 1)
        {
            _scenario = scenario;
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
            BuildTargets();
            if (IsOwn) BuildOwn();
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
                Set(rules, flag, !(IsOwn && _scenario is not ("own-accept-parts" or "own-secondary-self") && flag.StartsWith("Friendly", StringComparison.Ordinal)));
            if (_scenario == "own-secondary-self") { var secondary = Filter(EffectFilter.Defaults.Enemy); secondary.Self = true; secondary.Friendlies = true; Set(rules, "SecondaryFilter", secondary); }
            if (_scenario == "own-accept-shield") { var shields = Filter(EffectFilter.Defaults.Enemy); shields.Self = true; shields.Friendlies = true; Set(rules, "ShieldCollisions", shields); }
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
            if (_scenario is "secondary" or "crew-secondary" || IsOwn)
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
        private bool IsOwn => _scenario.StartsWith("own", StringComparison.Ordinal);

        /// <summary>
        /// Own-ship fixtures around the emitters (whole-body broad phase): structural low cells behind and beside the beam
        /// start, operational high cells in front of it, and (own-shield scenarios) an own arc shield over the start.
        /// </summary>
        internal void BuildOwn()
        {
            Body body = Source.Physics.Body!;
            body.UseWholeBodyBroadPhase = true;
            void Rect(int left, int top, int width, int height, Category category)
            {
                Fixture fixture = body.CreateRectangle(width, height, 1f, new System.Numerics.Vector2(left + width / 2f, top + height / 2f));
                fixture.CollisionCategories = category;
                fixture.UserData = new ShipPhysics.CellsFixtureInfo(new IntRect(left, top, width, height), 1f, fixture);
            }
            Rect(-12, -6, 12, 30, Category.Cat1 | Category.Cat3 | Category.Cat4);
            Rect(2, -6, 2, 30, Category.Cat1 | Category.Cat2 | Category.Cat5);
            Rect(5, -6, 1, 30, Category.Cat1 | Category.Cat3 | Category.Cat5);
            Rect(-14, -6, 2, 30, Category.Cat1); // outside every beam filter: IsShipHitValid would accept it, but no walk visits it
            if (_scenario is "own-shield" or "own-accept-shield") AddShield(Source, body, warm: true, at: new IntVector2(-2, 1));
            if (_scenario == "own-secondary-self") // real own parts under the cells, so the secondary part filter can accept them
                for (int y = -6; y < 24; y++) { AddPart(Source, new IntVector2(2, y), HealthType.Operational); AddPart(Source, new IntVector2(3, y), HealthType.Operational); }
        }

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
        private void AddShield(Ship target, Body body, bool warm, IntVector2? at = null)
        {
            var rules = Shell<PartRules>(); rules.Size = new IntVector2(1, 1); Set(rules, "HealthType", HealthType.Operational); Set(rules, "TypeCategories", new HashSet<ID<PartCategory>>());
            var part = Shell<Part>(); Set(part, "<Rules>k__BackingField", rules); Set(part, "p_ship", target); Set(part, "<Location>k__BackingField", at ?? new IntVector2(-8, 12));
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

        /// <summary>BeamFrozen preparation in native order (RNG drawn), then the worker query of every eligible record on fresh threads.</summary>
        internal Speculation Speculate(int workers, System.Collections.Concurrent.ConcurrentBag<bool>? held = null)
        {
            var s = new BeamFrozen.Session { Sim = Sim, Binding = new BeamFrozen.Binding { On = true, Workers = true } };
            foreach (BeamEmitter beam in Manager._emittingBeams) BeamFrozen.PrepareBeam(s, beam);

            var queue = new System.Collections.Concurrent.ConcurrentQueue<BeamFrozen.FrozenRecord>(s.Records.Where(r => r.Eligible));
            Thread[] threads = Enumerable.Range(0, workers).Select(_ => new Thread(() =>
            {
                while (queue.TryDequeue(out BeamFrozen.FrozenRecord? r)) BeamParallel.Run(r, frozen: false);
                held?.Add(BeamParallel.Ctx.Current.Holds);
            })).ToArray();
            foreach (Thread t in threads) t.Start();
            foreach (Thread t in threads) t.Join();
            return new Speculation(s);
        }

        internal (ulong, ulong) RandState() { Source.Rand.GetState(out ulong a, out ulong b); return (a, b); }

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

        public void Dispose()
        {
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
