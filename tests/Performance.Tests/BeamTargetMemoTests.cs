using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Cosmoteer;
using Cosmoteer.Crew;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Modes.Creative;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Blueprints;
using Cosmoteer.Ships.Commands;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Defenses;
using Cosmoteer.Ships.Parts.Weapons;
using Cosmoteer.Simulation;
using Cosmoteer.Simulation.HitEffects;
using Halfling;
using Halfling.Application;
using Halfling.Geometry;
using Halfling.Physics2D.Collision;
using Halfling.Physics2D.Collision.Shapes;
using Halfling.Physics2D.Common;
using Halfling.Physics2D.Dynamics;
using Halfling.Scene2D.Physics;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

public sealed class BeamTargetMemoTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1", true)]
    public void ExplicitOptInOnly(string? value, bool expected) => Assert.Equal(expected, BeamTargetMemo.IsRequested(value));

    [Fact]
    public void EntryStoresExplicitNullAndMatchesEveryIdentityDimension()
    {
        var entry = new BeamTargetMemo.Entry();
        var receiver = Shell<BeamEmitter>(); var provider = Shell<FixedWeapon>();
        entry.Store(receiver, provider, true, null);
        Assert.True(entry.TryGet(receiver, provider, true, out Ship? result)); Assert.Null(result);
        Assert.False(entry.TryGet(Shell<BeamEmitter>(), provider, true, out _));
        Assert.False(entry.TryGet(receiver, Shell<FixedWeapon>(), true, out _));
        Assert.False(entry.TryGet(receiver, provider, false, out _));
    }

    [Fact]
    public void InvalidatedEntryCannotBeResurrectedByLaterSuccessfulCall()
    {
        var entry = new BeamTargetMemo.Entry();
        var receiver = Shell<BeamEmitter>(); var provider = Shell<FixedWeapon>(); var target = Shell<Ship>();
        entry.Store(receiver, provider, true, target);
        Assert.True(entry.TryGet(receiver, provider, true, out _)); entry.Invalidate();
        Assert.False(entry.TryGet(receiver, provider, true, out _));
        entry.Store(receiver, provider, true, target);
        Assert.False(entry.TryGet(receiver, provider, true, out _));
    }

    [Fact]
    public void InstalledCoverageContainsEveryWorldQueryAndBothPreShapeWrappers()
    {
        var definitions = BeamTargetMemo.ReadDefinitions();
        var world = definitions.Where(d => d.Target.DeclaringType?.FullName == "Halfling.Physics2D.Dynamics.World").ToArray();
        Assert.Equal(7, world.Length);
        Assert.Equal(2, world.Count(d => d.Target.Name == "TestPointAll"));
        Assert.Contains(world, d => d.Target.Name == "QueryAABB");
        Assert.Contains(world, d => d.Target.Name == "RayCastCallbackBroadPhaseWrapper");
        Assert.Contains(world, d => d.Target.Name == "RayCastCallbackFixturePhaseWrapper");
        foreach (var definition in definitions) BeamTargetMemo.ValidateDefinition(definition);
    }

    [Theory]
    [InlineData("native")]
    [InlineData("verify")]
    [InlineData("verify-reentry")]
    [InlineData("verify-exception")]
    [InlineData("exception")]
    [InlineData("foreign")]
    [InlineData("late")]
    [InlineData("missing")]
    [InlineData("secondary")]
    [InlineData("verify-secondary")]
    public async Task IsolatedNativeCases(string scenario)
    {
        string? childCase = Environment.GetEnvironmentVariable("BEAM_MEMO_TEST_CASE");
        if (childCase != null) { if (childCase == scenario) RunNative(scenario); return; }
        var start = new ProcessStartInfo(Path.ChangeExtension(typeof(BeamTargetMemoTests).Assembly.Location, ".exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-method"); start.ArgumentList.Add("Performance.Tests.BeamTargetMemoTests.IsolatedNativeCases");
        start.ArgumentList.Add("-noLogo"); start.ArgumentList.Add("-noColor");
        start.Environment["PERFORMANCE_BEAM_TARGET_MEMO"] = "1";
        start.Environment["PERFORMANCE_BEAM_TARGET_VERIFY"] = scenario.StartsWith("verify", StringComparison.Ordinal) ? "1" : "0";
        start.Environment["BEAM_MEMO_TEST_CASE"] = scenario;
        await RunChild(start);
    }
    private static async Task RunChild(ProcessStartInfo start)
    {
        using Process child = Process.Start(start)!;
        Task<string> stdout = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(30_000);
        try
        {
            await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            Assert.True(child.ExitCode == 0, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } }
    }
    private static void RunNative(string scenario)
    {
        App.Director = Shell<Director>(); Set(App.Director, "<MainThread>k__BackingField", Thread.CurrentThread);
        if (scenario == "late") App.Director.FrameCount = 1;
        var own = new Harmony(Patcher.HarmonyId); var foreign = new Harmony("Performance.Tests.BeamMemo.Foreign");
        var definitions = BeamTargetMemo.ReadDefinitions();
        try
        {
            if (scenario != "missing")
                foreach (var d in definitions)
                {
                    HarmonyMethod? Hook(string name) => d.Patch.GetMethod(name, BindingFlags.Public | BindingFlags.Static) is { } m ? new HarmonyMethod(m) : null;
                    own.Patch(d.Target, prefix: Hook("Prefix"), transpiler: Hook("Transpiler"), finalizer: Hook("Finalizer"));
                    foreach (MethodBase parent in Patcher.FindR2RInliners(d.Target))
                    {
                        Assert.True(definitions.Any(known => known.Target.Module == parent.Module && known.Target.MetadataToken == parent.MetadataToken),
                            "Uncovered R2R caller of " + d.Target.DeclaringType!.FullName + "." + d.Target.Name + ": " + parent.DeclaringType?.FullName + "." + parent.Name + " token=" + parent.MetadataToken.ToString("X8"));
                        own.Patch(parent, transpiler: new HarmonyMethod(typeof(Patcher), nameof(Patcher.Rejit)));
                    }
                }
            if (scenario == "foreign") foreign.Patch(AccessTools.Method(typeof(Weapon), "GetShipTarget"), prefix: new HarmonyMethod(typeof(BeamTargetMemoTests), nameof(ForeignPrefix)));
            if (scenario is "missing" or "late" or "foreign")
            {
                Assert.False(BeamTargetMemo.Initialize()); Assert.False(BeamTargetMemo.Ready);
                Assert.Contains(scenario == "late" ? "FrameCount" : scenario == "foreign" ? "Foreign patch" : "coverage", BeamTargetMemo.FailureReason); return;
            }
            Assert.True(BeamTargetMemo.Initialize(), BeamTargetMemo.FailureReason);
            using var f = new Setup();
            Assert.True(BeamTargetMemo.BindOwnedSimulation(f.Sim));
            NativeConfirmedTargetsAndProhibition(f);
            if (scenario is "secondary" or "verify-secondary") { NativeSecondaryFilter(f); return; }
            if (scenario == "verify") { VerifyMismatch(f); return; }
            if (scenario == "verify-reentry") { VerifyReentry(f); return; }
            if (scenario == "verify-exception") { VerifyExceptionAfterHit(f); return; }
            if (scenario == "exception") { VerifyException(f); return; }
            NativeNullAndScopeGating(f);
            DirtyFirstCannotWarmIntoCache(f);
            DirtyOrUnknownAfterEntryNeverFills(f);
            EveryNestedWorldEntryInvalidates(f);
            UnknownShapeMissInvalidatesBeforeNative(f);
            BodyTreeRejectedBeforeNativeTraversal(f);
            PartFilterInvalidatesBeforeCustomComparer(f);
            ShieldCrewAndNestedScopesInvalidate(f);
            DisableUnwinds(f);
        }
        finally { BeamTargetMemo.Disable(); foreign.UnpatchAll(foreign.Id); own.UnpatchAll(own.Id); PhysicsExtensions.s_dataCondition = null; }
    }
    private static void ForeignPrefix() { }

    private static void NativeConfirmedTargetsAndProhibition(Setup f)
    {
        var targets = f.Provider.ExplicitTargets._targets;
        var target = Shell<WeaponTarget>(); Set(target, "<IsConfirmed>k__BackingField", true); Set(target, "<AllowFriendly>k__BackingField", true);
        targets.Add(target);
        try
        {
            // Exact native confirmed Ship target: CanAttack/Exists/sight, effective range and coverage are evaluated.
            Set(target, "<Target>k__BackingField", f.Ship);
            Assert.True(f.Provider.DetWorldLocation.DistanceTo(f.Ship.DetWorldCenter) > 0f);
            AssertNativeMemoResult(f, f.Ship);
            f.Receiver.Rules.Range.BaseValue = 0f;
            AssertNativeMemoResult(f, null); // Same target fails the native nonzero-distance range check.
            f.Receiver.Rules.Range.BaseValue = 1000f;

            // Exact native Part target with a non-null prohibition filter. Both paths execute allegiance and category
            // comparisons; only the filter category changes between the separate complete raycast scopes.
            Set(target, "<Target>k__BackingField", f.Part);
            var filter = f.Filter(); filter.Ships = true;
            ID<PartCategory> present = new("beam_memo_test_present"), absent = new("beam_memo_test_absent");
            f.Part.Rules.TypeCategories.Add(present);
            filter.OnlyPartCategories = new HashSet<ID<PartCategory>> { absent };
            f.Provider.Rules.ProhibitTargetPartsFilter = filter;
            Assert.False(filter.ValidateHitShipAndPart(f.Ship, f.Part, f.Ship, null, f.Ship));
            AssertNativeMemoResult(f, f.Ship); // Category mismatch: the target is allowed.
            filter.OnlyPartCategories.Clear(); filter.OnlyPartCategories.Add(present);
            Assert.True(filter.ValidateHitShipAndPart(f.Ship, f.Part, f.Ship, null, f.Ship));
            AssertNativeMemoResult(f, null); // Matching category: the target is prohibited.
        }
        finally { targets.Clear(); f.Provider.Rules.ProhibitTargetPartsFilter = null; f.Part.Rules.TypeCategories.Clear(); f.Receiver.Rules.Range.BaseValue = 1000f; }
    }
    private static void AssertNativeMemoResult(Setup f, Ship? expected)
    {
        Assert.Same(expected, f.Native(f.Receiver, true));
        long calls = BeamTargetMemo.NativeCalls, fills = BeamTargetMemo.EligiblePureFills, hits = BeamTargetMemo.Hits;
        using var scope = new Scope(f);
        Assert.Same(expected, BeamTargetMemo.Invoke(f.Receiver, true, f.Native));
        Assert.Same(expected, BeamTargetMemo.Invoke(f.Receiver, true, f.Native));
        Assert.Equal(calls + (BeamTargetMemo.VerificationRequested ? 2 : 1), BeamTargetMemo.NativeCalls);
        Assert.Equal(fills + 1, BeamTargetMemo.EligiblePureFills); Assert.Equal(hits + 1, BeamTargetMemo.Hits);
        Assert.False(scope.Query.Value.Invalid);
    }

    private static void NativeSecondaryFilter(Setup f)
    {
        var filter = new EffectFilter(EffectFilter.Defaults.Enemy) { Crew = false, StructuralHealth = false, Self = false };
        Set(f.Receiver.Rules, "SecondaryFilter", filter);
        var target = Shell<WeaponTarget>(); Set(target, "<IsConfirmed>k__BackingField", true);
        Set(target, "<AllowFriendly>k__BackingField", true); Set(target, "<Target>k__BackingField", f.Ship);
        f.Provider.ExplicitTargets._targets.Add(target);
        var players = Assert.IsType<GameRoot>(f.Sim.Game)._playerStates;
        var hit = (Ship)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(f.Ship, null)!;
        var metadata = Shell<ShipMetadata>(); Set(metadata, "<PlayerIndex>k__BackingField", 1); Set(hit, "<Metadata>k__BackingField", metadata);
        players.Add(1, Shell<PlayerState>());
        var forward = AccessTools.Method(typeof(BeamTargetMemo), "FilterPart");
        int playerCount = players.Count;
        foreach (bool invulnerable in new[] { false, true })
        {
            hit.SelfInvulnerable = invulnerable;
            using var scope = new Scope(f);
            long fills = BeamTargetMemo.EligiblePureFills, hits = BeamTargetMemo.Hits;
            Assert.Same(f.Ship, BeamTargetMemo.Invoke(f.Receiver, true, f.Native));
            Body body = f.World.CreateCircle(0.5f, 1f); body.UserData = hit;
            body.FixtureList[0].UserData = Shell<ShipPhysics.CellsFixtureInfo>();
            try
            {
                BeamTargetMemo.FilterHook.Prefix(f.Receiver, body.FixtureList[0]);
                // Native nullable allegiance falls through AreEnemies/AreAllies and CanDamage/CanAttack.
                Assert.Equal(!invulnerable, filter.ValidateHitShip(hit, f.Ship, 0, hit));
                Assert.True((bool)forward.Invoke(null, [filter, f.Part, HitEffectFlags.None, false])!);
                Assert.False(scope.Query.Value.Invalid);
                Assert.Same(f.Ship, BeamTargetMemo.Invoke(f.Receiver, true, f.Native));
                Assert.Equal(fills + 1, BeamTargetMemo.EligiblePureFills); Assert.Equal(hits + 1, BeamTargetMemo.Hits);
                Assert.Equal(playerCount, players.Count);
            }
            finally { f.World.Remove(body); }
        }
        using (var scope = new Scope(f))
        {
            BeamTargetMemo.Invoke(f.Receiver, true, f.Native);
            var other = new EffectFilter(EffectFilter.Defaults.Enemy);
            _ = forward.Invoke(null, [other, f.Part, HitEffectFlags.None, false]);
            Assert.True(scope.Query.Value.Invalid); // Equal shape does not authorize another filter instance.
        }
        f.Provider.Rules.ProhibitTargetPartsFilter = filter;
        using (var scope = new Scope(f, pure: false))
        {
            Assert.True(scope.Query.Value.Invalid);
        }
        f.Provider.Rules.ProhibitTargetPartsFilter = null;
        using (var scope = new Scope(f))
        {
            BeamTargetMemo.Invoke(f.Receiver, true, f.Native);
            var sources = (Stack<ActionSourcePlayer?>)AccessTools.Field(typeof(SimRoot), "_sourcePlayerStack").GetValue(f.Sim)!;
            sources.Push(new ActionSourcePlayer(99, null));
            Body body = f.World.CreateCircle(0.5f, 1f); body.UserData = hit;
            body.FixtureList[0].UserData = Shell<ShipPhysics.CellsFixtureInfo>();
            try
            {
                BeamTargetMemo.FilterHook.Prefix(f.Receiver, body.FixtureList[0]);
                Assert.True(scope.Query.Value.Invalid); Assert.False(players.ContainsKey(99));
                _ = forward.Invoke(null, [filter, f.Part, HitEffectFlags.None, false]);
                Assert.True(scope.Query.Value.Invalid); Assert.False(players.ContainsKey(99));
            }
            finally { sources.Pop(); f.World.Remove(body); }
        }
        using (var scope = new Scope(f))
        {
            BeamTargetMemo.Invoke(f.Receiver, true, f.Native);
            players.Remove(1);
            Body body = f.World.CreateCircle(0.5f, 1f); body.UserData = hit;
            body.FixtureList[0].UserData = Shell<ShipPhysics.CellsFixtureInfo>();
            try
            {
                BeamTargetMemo.FilterHook.Prefix(f.Receiver, body.FixtureList[0]);
                Assert.True(scope.Query.Value.Invalid); Assert.False(players.ContainsKey(1));
                var query = scope.Query; var native = f.Native;
                Assert.Same(f.Ship, BeamTargetMemo.Invoke(f.Receiver, true, (receiver, argument) =>
                { Assert.True(query.Value.Invalid); Assert.False(players.ContainsKey(1)); return native(receiver, argument); }));
                Assert.Equal(playerCount - 1, players.Count);
                hit.SelfInvulnerable = false;
                Assert.True(filter.ValidateHitShip(hit, f.Ship, 0, hit));
                Assert.True(players.ContainsKey(1)); // Original fallback retains its native state-creation semantics.
                players.Remove(1);
            }
            finally { f.World.Remove(body); }
        }
        using (var scope = new Scope(f))
        {
            BeamTargetMemo.Invoke(f.Receiver, true, f.Native);
            players.Remove(0);
            _ = forward.Invoke(null, [filter, f.Part, HitEffectFlags.None, false]);
            Assert.True(scope.Query.Value.Invalid); Assert.Empty(players);
        }
    }

    private static void NativeNullAndScopeGating(Setup f)
    {
        BeamTargetMemo.SecondaryHook.Prefix(f.Receiver, out var unscoped); Assert.Null(unscoped);
        using (var scope = new Scope(f))
        {
            long calls = BeamTargetMemo.NativeCalls, hits = BeamTargetMemo.Hits;
            Assert.Null(BeamTargetMemo.Invoke(f.Receiver, true, f.Native));
            Assert.Null(BeamTargetMemo.Invoke(f.Receiver, true, f.Native));
            Assert.Equal(calls + 1, BeamTargetMemo.NativeCalls); Assert.Equal(hits + 1, BeamTargetMemo.Hits);
            f.Receiver._getTargetFrom = Shell<FixedWeapon>();
            int forwarded = 0;
            Assert.Null(BeamTargetMemo.Invoke(f.Receiver, true, (_, _) => { forwarded++; return null; }));
            Assert.Equal(1, forwarded); Assert.True(scope.Query.Value.Invalid);
            f.Receiver._getTargetFrom = f.Provider;
        }
        f.Sim.IsDoingParallelUpdate = true;
        BeamTargetMemo.EmitHook.Prefix(f.Manager, out var outside);
        BeamTargetMemo.SecondaryHook.Prefix(f.Receiver, out var parallel); Assert.Null(parallel);
        BeamTargetMemo.EmitHook.Finalizer(outside, null); f.Sim.IsDoingParallelUpdate = false;
    }
    private static void DirtyFirstCannotWarmIntoCache(Setup f)
    {
        Set(f.Parts, "_isBoundingCircleDirty", true);
        using var scope = new Scope(f, pure: false);
        var first = Shell<Ship>(); var second = Shell<Ship>(); Ship current = first; int calls = 0;
        Ship? Native(Emitter _, bool __) { calls++; Set(f.Parts, "_isBoundingCircleDirty", false); Ship old = current; current = second; return old; }
        Assert.Same(first, BeamTargetMemo.Invoke(f.Receiver, true, Native));
        Assert.Same(second, BeamTargetMemo.Invoke(f.Receiver, true, Native));
        Assert.Equal(2, calls); Assert.True(scope.Query.Value.Invalid);
    }
    private static void EveryNestedWorldEntryInvalidates(Setup f)
    {
        Action[] queries =
        [
            () => f.World.RayCast((_, _, _, _) => 1f, new(-2, 0), new(4, 0)),
            () => { var box = new AABB(new System.Numerics.Vector2(-1), new System.Numerics.Vector2(1)); f.World.QueryAABB(_ => true, ref box); },
            () => f.World.TestPoint(default), () => f.World.TestPointAll(default),
            () => f.World.TestPointAll(default, new List<Fixture>()),
        ];
        foreach (Action query in queries)
        {
            using var scope = new Scope(f);
            int calls = 0;
            Ship? Native(Emitter _, bool __) { calls++; return null; }
            BeamTargetMemo.Invoke(f.Receiver, true, Native); query(); BeamTargetMemo.Invoke(f.Receiver, true, Native);
            Assert.Equal(2, calls); Assert.True(scope.Query.Value.Invalid);
        }
    }
    private static void DirtyOrUnknownAfterEntryNeverFills(Setup f)
    {
        using (var scope = new Scope(f))
        {
            Set(f.Parts, "_isBoundingCircleDirty", true);
            var query = scope.Query; var parts = f.Parts;
            int calls = 0;
            Ship? Native(Emitter _, bool __) { calls++; Assert.True(query.Value.Invalid); Set(parts, "_isBoundingCircleDirty", false); return null; }
            BeamTargetMemo.Invoke(f.Receiver, true, Native); BeamTargetMemo.Invoke(f.Receiver, true, Native);
            Assert.Equal(2, calls);
        }
        using (var scope = new Scope(f))
        {
            var target = Shell<WeaponTarget>(); Set(target, "<IsConfirmed>k__BackingField", true); Set(target, "<Target>k__BackingField", Shell<SpaceCrew>());
            f.Provider.ExplicitTargets._targets.Add(target);
            var query = scope.Query; var targets = f.Provider.ExplicitTargets._targets;
            int calls = 0;
            Ship? Native(Emitter _, bool __) { calls++; Assert.True(query.Value.Invalid); targets.Clear(); return null; }
            BeamTargetMemo.Invoke(f.Receiver, true, Native); BeamTargetMemo.Invoke(f.Receiver, true, Native);
            Assert.Equal(2, calls);
        }
    }
    private static void UnknownShapeMissInvalidatesBeforeNative(Setup f)
    {
        using var scope = new Scope(f);
        var query = scope.Query;
        int calls = 0, shapes = 0;
        Ship? Native(Emitter _, bool __) { calls++; return null; }
        BeamTargetMemo.Invoke(f.Receiver, true, Native);
        Body body = f.World.CreateBody();
        var shape = new CallbackCircle(() => { shapes++; Assert.True(query.Value.Invalid); });
        Fixture fixture = body.CreateFixture(shape); body.UserData = f.Ship; f.World.ProcessChanges();
        var input = new RayCastInput { Point1 = new(-2, 0), Point2 = new(4, 0), MaxFraction = 1f };
        var wrapper = AccessTools.Method(typeof(World), "RayCastCallbackBroadPhaseWrapper");
        _ = wrapper.Invoke(f.World, [input, Category.All, fixture.Proxies[0].ProxyId, null]);
        Assert.Equal(1, shapes); BeamTargetMemo.Invoke(f.Receiver, true, Native); Assert.Equal(2, calls);
        f.World.Remove(body);
    }
    private static void PartFilterInvalidatesBeforeCustomComparer(Setup f)
    {
        using var scope = new Scope(f);
        var query = scope.Query;
        int calls = 0;
        Ship? Native(Emitter _, bool __) { calls++; return null; }
        BeamTargetMemo.Invoke(f.Receiver, true, Native);
        var comparer = new CallbackComparer(() => Assert.True(query.Value.Invalid));
        var filter = f.Filter(); filter.OnlyPartCategories = new HashSet<ID<PartCategory>>(comparer) { default };
        f.Part.Rules.TypeCategories.Add(default);
        comparer.Armed = true;
        var forward = AccessTools.Method(typeof(BeamTargetMemo), "FilterPart");
        Assert.True((bool)forward.Invoke(null, [filter, f.Part, HitEffectFlags.None, false])!);
        BeamTargetMemo.Invoke(f.Receiver, true, Native); Assert.Equal(2, calls); Assert.True(comparer.Calls > 0);
    }
    private static void BodyTreeRejectedBeforeNativeTraversal(Setup f)
    {
        using var scope = new Scope(f);
        BeamTargetMemo.Invoke(f.Receiver, true, f.Native);
        Body body = f.World.CreateBody(); body.UseWholeBodyBroadPhase = true; body.UserData = f.Ship; f.World.ProcessChanges();
        Set(body, "<FixtureTree>k__BackingField", new DerivedTree());
        var input = new RayCastInput { Point1 = new(-2, 0), Point2 = new(4, 0), MaxFraction = 1f };
        var wrapper = AccessTools.Method(typeof(World), "RayCastCallbackBroadPhaseWrapper");
        _ = wrapper.Invoke(f.World, [input, Category.All, body._proxyId, null]);
        Assert.True(scope.Query.Value.Invalid); f.World.Remove(body);
    }
    private static void ShieldCrewAndNestedScopesInvalidate(Setup f)
    {
        foreach ((object bodyData, object? fixtureData) in new (object, object?)[] { (f.Ship, Shell<ArcShield>()), (Shell<SpaceCrew>(), null) })
        {
            using var scope = new Scope(f);
            Body body = f.World.CreateCircle(0.5f, 1f); f.World.ProcessChanges(); body.UserData = bodyData; body.FixtureList[0].UserData = fixtureData;
            BeamTargetMemo.FilterHook.Prefix(f.Receiver, body.FixtureList[0]); Assert.True(scope.Query.Value.Invalid);
            f.World.Remove(body);
        }
        using var outer = new Scope(f);
        BeamTargetMemo.SecondaryHook.Prefix(f.Receiver, out var inner);
        Assert.NotNull(inner); Assert.True(inner.Value.Invalid); Assert.True(outer.Query.Value.Invalid);
        BeamTargetMemo.SecondaryHook.Finalizer(inner, null);
    }
    private static void DisableUnwinds(Setup f)
    {
        using var scope = new Scope(f);
        BeamTargetMemo.Invoke(f.Receiver, true, f.Native);
        BeamTargetMemo.Disable(); Assert.True(scope.Query.Value.Invalid);
        int calls = 0; BeamTargetMemo.Invoke(f.Receiver, true, (_, _) => { calls++; return null; }); Assert.Equal(1, calls);
    }
    private static void VerifyMismatch(Setup f)
    {
        using var scope = new Scope(f); var changed = Shell<Ship>(); int calls = 0;
        long comparisons = BeamTargetMemo.VerificationComparisons;
        Ship? Native(Emitter _, bool __) => ++calls == 1 ? null : changed;
        Assert.Null(BeamTargetMemo.Invoke(f.Receiver, true, Native));
        Assert.Same(changed, BeamTargetMemo.Invoke(f.Receiver, true, Native));
        Assert.Equal(2, calls); Assert.Equal(comparisons + 1, BeamTargetMemo.VerificationComparisons); Assert.Equal(1, BeamTargetMemo.VerificationMismatches);
        Assert.False(BeamTargetMemo.Ready); Assert.True(scope.Query.Value.Invalid);
    }
    private static void VerifyException(Setup f)
    {
        using var scope = new Scope(f); var expected = new InvalidOperationException("native memo test");
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => BeamTargetMemo.Invoke(f.Receiver, true, (_, _) => throw expected)));
        Assert.False(BeamTargetMemo.Ready); Assert.True(scope.Query.Value.Invalid);
    }
    private static void VerifyExceptionAfterHit(Setup f)
    {
        using var scope = new Scope(f); var expected = new InvalidOperationException("verification native exception");
        Assert.Null(BeamTargetMemo.Invoke(f.Receiver, true, (_, _) => null));
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => BeamTargetMemo.Invoke(f.Receiver, true, (_, _) => throw expected)));
        Assert.False(BeamTargetMemo.Ready); Assert.True(scope.Query.Value.Invalid); Assert.False(scope.Query.Calling);
    }
    private static void VerifyReentry(Setup f)
    {
        using var scope = new Scope(f); int outerCalls = 0, nestedCalls = 0;
        var query = scope.Query; var receiver = f.Receiver;
        Ship? Native(Emitter _, bool __)
        {
            outerCalls++;
            if (outerCalls == 2)
                Assert.Null(BeamTargetMemo.Invoke(receiver, true, (_, _) => { nestedCalls++; Assert.True(query.Value.Invalid); return null; }));
            return null;
        }
        Assert.Null(BeamTargetMemo.Invoke(f.Receiver, true, Native)); Assert.Null(BeamTargetMemo.Invoke(f.Receiver, true, Native));
        Assert.Equal(2, outerCalls); Assert.Equal(1, nestedCalls); Assert.True(scope.Query.Value.Invalid); Assert.False(scope.Query.Calling);
    }

    private sealed class Scope : IDisposable
    {
        private readonly WeaponManager? _previous;
        private readonly int _world;
        internal readonly BeamTargetMemo.Query Query;
        internal Scope(Setup f, bool pure = true)
        {
            BeamTargetMemo.EmitHook.Prefix(f.Manager, out _previous);
            BeamTargetMemo.SecondaryHook.Prefix(f.Receiver, out var query); Query = Assert.IsType<BeamTargetMemo.Query>(query);
            Assert.Equal(pure, BeamTargetMemo.PureBefore(Query));
            Assert.Equal(!pure, Query.Value.Invalid);
            PhysicsExtensions.s_dataCondition = AccessTools.Method(typeof(BeamEmitter), "ValidateSecondaryHit").CreateDelegate<Func<Fixture, Vector2, (bool, object?)>>(f.Receiver);
            MethodInfo method = (MethodInfo)typeof(PhysicsExtensions).Module.ResolveMethod(0x06005C05)!;
            object? target = method.DeclaringType!.GetField("<>9", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
            var callback = method.CreateDelegate<Func<Fixture, System.Numerics.Vector2, System.Numerics.Vector2, float, float>>(target);
            BeamTargetMemo.WorldRayHook.Prefix(f.World, callback, out _world);
        }
        public void Dispose()
        {
            BeamTargetMemo.WorldRayHook.Finalizer(_world, null);
            BeamTargetMemo.SecondaryHook.Finalizer(Query, null);
            BeamTargetMemo.EmitHook.Finalizer(_previous, null);
            Assert.Null(Query.Receiver); Assert.Null(Query.Provider); Assert.Null(Query.Sim); Assert.All(Query.RelevantShips, Assert.Null);
            PhysicsExtensions.s_dataCondition = null;
        }
    }
    private sealed class Setup : IDisposable
    {
        internal readonly BeamEmitter Receiver = Shell<BeamEmitter>();
        internal readonly FixedWeapon Provider = Shell<FixedWeapon>();
        internal readonly Part Part = Shell<Part>();
        internal readonly PartsManager Parts = Shell<PartsManager>();
        internal readonly Ship Ship = Shell<Ship>();
        internal readonly SimRoot Sim = Shell<SimRoot>();
        internal readonly WeaponManager Manager = Shell<WeaponManager>();
        internal readonly World World = new(new DynamicTreeBroadPhase<BodyFixtureProxy>());
        internal readonly Func<Emitter, bool, Ship?> Native = AccessTools.Method(typeof(Emitter), "GetShipTarget").CreateDelegate<Func<Emitter, bool, Ship?>>();
        internal EffectFilter Filter()
        {
            var filter = Shell<EffectFilter>(); filter.IgnoreInvulnerability = true;
            filter.Friendlies = filter.Enemies = filter.Neutrals = filter.Junk = true; filter.OperationalHealth = true; filter.StructuralHealth = true;
            return filter;
        }
        internal Setup()
        {
            Set(Receiver, "<Part>k__BackingField", Part); Set(Provider, "<Part>k__BackingField", Part); Set(Part, "p_ship", Ship);
            Set(Ship, "_root", Sim); Sim.SceneThread = Thread.CurrentThread; Sim.Game = Shell<GameRoot>(); Sim.Game.NetManager = Shell<SPManager>();
            Set(Sim, "_sourcePlayerStack", new Stack<ActionSourcePlayer?>());
            Set(Sim, "<Mode>k__BackingField", Shell<CreativeSimModeManager>()); Set(Sim.Mode, "<Sim>k__BackingField", Sim);
            Set(Sim.Game, "<Mode>k__BackingField", Shell<CreativeGameModeManager>());
            var rules = Shell<BeamEmitterRules>(); Set(rules, "Range", Shell<ModifiableFloat>()); rules.Range.BaseValue = 1000f; Set(rules, "SecondaryFilter", Filter());
            Set(Receiver, "<Rules>k__BackingField", rules); Set(Provider, "<Rules>k__BackingField", Shell<FixedWeaponRules>());
            Receiver._getTargetFrom = Provider; Provider._targetingEmitter = Receiver;
            Set(Part, "<Rules>k__BackingField", Shell<PartRules>()); Set(Part.Rules, "TypeCategories", new HashSet<ID<PartCategory>>());
            Set(Part, "p_health", 1); Part.Rules.Size = new IntVector2(1, 1); Set(Ship, "p_scale", Vector2.One);
            Set(Ship, "<Parts>k__BackingField", Parts); Set(Parts, "_cachedBoundingCircle", new Circle(new Vector2(10f, 0f), 1f));
            Set(Parts, "<Ship>k__BackingField", Ship);
            Set(Parts, "_partsByCell", new Dictionary<IntVector2, Part>()); Set(Parts, "_orderedParts", new List<Part> { Part });
            var metadata = Shell<ShipMetadata>(); Set(Ship, "<Metadata>k__BackingField", metadata);
            Set(metadata, "<Ship>k__BackingField", Ship);
            Set(metadata, "<CustomWeaponCoverages>k__BackingField", new Dictionary<(PartSaveData, ID<PartComponentRules>), MPValue<WeaponCoverage>>());
            Set(Ship, "<Weapons>k__BackingField", Manager); Set(Manager, "<Ship>k__BackingField", Ship); Manager._node = Ship;
            var targets = Shell<TargetManager>(); Set(targets, "<Weapon>k__BackingField", Provider); Set(targets, "_targets", new List<WeaponTarget>()); Set(Provider, "<ExplicitTargets>k__BackingField", targets);
            var commands = Shell<CommandManager>(); Set(commands, "_commands", new List<Command>()); Set(Ship, "<Commands>k__BackingField", commands);
            Set(Sim.Game, "_playerStates", new SortedDictionary<int, PlayerState> { [0] = Shell<PlayerState>() });
            var ships = Shell<SimShipsManager>(); Set(Sim, "<Ships>k__BackingField", ships); ships._sightCircleRefreshBuster = 1;
            var sight = Shell<SimShipsManager.TeamSightCircles>(); sight.RefreshNum = 1;
            Set(ships, "_detSightCirclesByTeam", new ConcurrentDictionary<int, SimShipsManager.TeamSightCircles>(new[] { new KeyValuePair<int, SimShipsManager.TeamSightCircles>(0, sight) }));
            Set(Sim, "<PhysicsWorld>k__BackingField", World); Set(Ship, "<Physics>k__BackingField", Shell<ShipPhysics>());
            Set(Ship.Physics, "<Ship>k__BackingField", Ship);
            Set(Ship.Physics, "<PhysicsWrapper>k__BackingField", Shell<ScenePhysicsWrapper>());
        }
        public void Dispose() => World.Clear();
    }
    private sealed class CallbackCircle(Action callback) : CircleShape(0.5f, 1f)
    {
        public override Shape Clone() => new CallbackCircle(callback);
        public override bool RayCast(out RayCastOutput output, ref RayCastInput input, ref Transform transform, int childIndex) { callback(); output = default; return false; }
    }
    private sealed class DerivedTree : DynamicTree<BodyFixtureProxy>;
    private sealed class CallbackComparer(Action callback) : IEqualityComparer<ID<PartCategory>>
    {
        internal bool Armed; internal int Calls;
        public bool Equals(ID<PartCategory> x, ID<PartCategory> y) => x.Equals(y);
        public int GetHashCode(ID<PartCategory> value) { if (Armed) { Calls++; callback(); } return value.GetHashCode(); }
    }
    private static void Set(object instance, string name, object? value)
    {
        for (Type? type = instance.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo? field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field == null) continue; field.SetValue(instance, value); return;
        }
        throw new MissingFieldException(instance.GetType().FullName, name);
    }

    private static T Shell<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
}
