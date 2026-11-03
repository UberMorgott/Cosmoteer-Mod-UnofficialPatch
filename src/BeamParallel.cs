using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Cosmoteer;
using Cosmoteer.Bullets;
using Cosmoteer.Bullets.Hits;
using Cosmoteer.Crew;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Modes;
using Cosmoteer.Factions;
using Cosmoteer.Modes.Career;
using Cosmoteer.Modes.Creative;
using Cosmoteer.Modes.Pvp;
using Cosmoteer.Modes.Pvp.BuildBattle;
using Cosmoteer.Modes.Pvp.Domination;
using Cosmoteer.Modes.Pvp.Elimination;
using Cosmoteer.Modes.Pvp.Pointmatch.Arena;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Blueprints;
using Cosmoteer.Ships.Commands;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Colliders;
using Cosmoteer.Ships.Parts.Defenses;
using Cosmoteer.Ships.Parts.Logic;
using Cosmoteer.Ships.Parts.Weapons;
using Cosmoteer.Simulation;
using Halfling;
using Halfling.Geometry;
using Halfling.Physics2D.Collision;
using Halfling.Physics2D.Collision.Shapes;
using Halfling.Physics2D.Common;
using Halfling.Physics2D.Common.Maths;
using Halfling.Physics2D.Dynamics;
using Halfling.Pooling;
using Halfling.Scene2D;
using HarmonyLib;
using V = Halfling.Geometry.Vector2;
using NV = System.Numerics.Vector2;
using HitInfo = Cosmoteer.Ships.Parts.Weapons.BeamEmitter.HitInfo;
using HitObject = Cosmoteer.Ships.Parts.Weapons.BeamEmitter.HitObject;
using StructuralHitInfo = Cosmoteer.Ships.Parts.Weapons.BeamEmitter.StructuralHitInfo;

namespace UnofficialPatch;

/// <summary>
/// Audited beam hit query shared by <see cref="BeamFrozen"/> (worker queries) and <see cref="BeamOwnBodySkip"/> (eligibility
/// checks): a handwritten copy of <c>BeamEmitter.GetHitInfo</c> lines 1082-1137 (<see cref="Ctx"/>) that reads only frozen
/// sim state through audited code (own traversal wrappers that check the fixture shape before native Shape.RayCast, own
/// copies of the hit callbacks using per-record spawn point, target and rule values), writes nothing outside its record,
/// and stops at any unaudited object (derived crew type, cold shield, unknown shape or user data, missing player state).
/// Exact SpaceCrew hits are handled (EffectFilter.ValidateHitCrew reads only frozen fields and known player states).
/// The former speculative replay mode (UNOFFICIAL_PATCH_BEAM_PARALLEL) was removed: no measured gain.
/// </summary>
internal static class BeamParallel
{
    private static readonly Guid GameMvid = new("fd7c0bba-64d3-40b4-a571-bf8b34eb4277");
    private static readonly Guid HalflingMvid = new("29d04acb-ae49-4f3b-8499-f03c9144a3a9");
    private const string GameFileSha = "F9451FBED4A11396C816D3F44EBDD836E48BCE68BA6AEAE28C3E534E037207D4";
    private const string HalflingFileSha = "80213C7C8182CE63B9075A48DFD80A7614B1FA2AB3BCD16A69D5363CF9A4B391";
    internal const string NativeIlSha256 = "A0E218BC5CA1B96C23CEE2CE1528A830E216F1075B719FA003AE8BF6E3212484";

    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    internal static readonly MethodInfo BucketTarget = AccessTools.Method(typeof(SceneRoot), "FixedUpdateForBucket", [typeof(int)]);
    internal static readonly MethodInfo EmitTarget = typeof(BeamEmitter).GetMethods(All).Single(m => m.Name.EndsWith(".DoEmitBeam", StringComparison.Ordinal));
    internal static readonly MethodInfo EmitBeamsMethod = AccessTools.Method(typeof(WeaponManager), "EmitBeams");
    private static readonly MethodInfo NativeValidateHit = AccessTools.Method(typeof(BeamEmitter), "ValidateHit");
    internal static readonly AccessTools.FieldRef<BeamEmitter, EventHandler<EventArgs>?> BeamValueChanged = AccessTools.FieldRefAccess<BeamEmitter, EventHandler<EventArgs>?>("e_valueChanged");
    internal static readonly AccessTools.FieldRef<BeamEmitter, EventHandler<TriggerEventArgs>?> BeamTrigger = AccessTools.FieldRefAccess<BeamEmitter, EventHandler<TriggerEventArgs>?>("HitIntervalElapsed");

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
        // Mode overrides reachable from the worker path (AuditedModes): allegiance, damage and invulnerability reads.
        foreach (string name in new[] { "AreAllies", "CanAttack", "IsInvulnerable", "IsHumanPlayerShip", "IsHumanPlayerAllegiance", "GetTeamFromPlayerIndex" })
            yield return M(typeof(SimModeManager), name);
        yield return M(typeof(GameRoot), "GetTeamFromPlayerIndex");
        yield return typeof(GameRoot).GetMethod("GetPlayerState", All, [typeof(int), typeof(bool)]) ?? throw new MissingMethodException(nameof(GameRoot), "GetPlayerState");
        foreach (string name in new[] { "CanBeDamagedBy", "IsEnemiesWith" })
            foreach (MethodInfo m in typeof(Ship).GetMethods(All).Where(m => m.Name == name).OrderBy(m => m.MetadataToken)) yield return m;
        Type career = typeof(CareerSimModeManager);
        foreach (string name in new[] { "AreEnemies", "CanAttack", "IsInvulnerable", "IsHumanPlayerAllegiance", "AreTruced", "HasProtectionAgreement", "get_CombatDifficulty" })
            yield return M(career, name);
        foreach (MethodInfo local in Locals(career, "HasProtectionAgreement")) yield return local;
        yield return M(typeof(CareerGameModeManager), "IsHumanPlayerIndex");
        yield return M(typeof(WantedManager), "IsPlayerWantedBy");
        yield return typeof(FactionRules).GetMethod("GetFaction", All, [typeof(int)]) ?? throw new MissingMethodException(nameof(FactionRules), "GetFaction");
        yield return M(typeof(PlayerIndexes), "IsFactionMilitary");
        yield return M(typeof(PlayerIndexes), "IsFactionCivilian");
        yield return M(typeof(PvpSimModeManager), "CanAttack");
        yield return M(typeof(PvpSimModeManager), "CanDamage");
        yield return M(typeof(BuildBattleSimModeManager), "IsInvulnerable");
        yield return M(typeof(Ship), "get_IsInvulnerable");
        yield return M(typeof(Ship), "get_IsHumanPlayerShip");
        yield return M(typeof(Ship), "ExistsInSim");
        yield return M(typeof(CrewSoul), "get_HomeShip");
        yield return M(typeof(CrewSoul), "get_PlayerIndex");
        yield return M(typeof(SpaceCrew), "get_Soul");
        yield return M(typeof(SimNode), "get_Sim");
        yield return M(typeof(SimObject), "get_UniqueID");
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
    private static readonly Dictionary<MethodBase, byte[]>? HarmonyState =
        AccessTools.Field(typeof(Harmony).Assembly.GetType("HarmonyLib.HarmonySharedState"), "state")?.GetValue(null) as Dictionary<MethodBase, byte[]>;
    /// <summary>
    /// Harmony's shared state (method -&gt; serialized patch info) as last seen by one consumer. Every patch, unpatch or
    /// retarget stores a new serialized array (HarmonySharedState.UpdatePatchInfo), so any change fails the reference
    /// comparison. Compared under Harmony's own lock; allocation-free. An unreadable state never matches.
    /// </summary>
    internal sealed class HarmonySnapshot
    {
        private MethodBase[] _keys = [];
        private byte[][] _values = [];
        private int _count = -1;

        internal void Reset() => _count = -1;

        /// <summary>True only when the state holds exactly the method and patch-info array references seen at <see cref="Remember"/>, in order.</summary>
        internal bool Unchanged()
        {
            Dictionary<MethodBase, byte[]>? state = HarmonyState;
            if (state == null) return false;
            lock (state)
            {
                if (state.Count != _count) return false;
                int i = 0;
                foreach (KeyValuePair<MethodBase, byte[]> entry in state)
                {
                    if (!ReferenceEquals(entry.Key, _keys[i]) || !ReferenceEquals(entry.Value, _values[i])) return false;
                    i++;
                }
                return true;
            }
        }

        internal void Remember()
        {
            Dictionary<MethodBase, byte[]>? state = HarmonyState;
            if (state == null) return;
            lock (state)
            {
                if (_keys.Length < state.Count) { _keys = new MethodBase[state.Count * 2]; _values = new byte[state.Count * 2][]; }
                int i = 0;
                foreach (KeyValuePair<MethodBase, byte[]> entry in state) { _keys[i] = entry.Key; _values[i] = entry.Value; i++; }
                _count = state.Count;
            }
        }
    }
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

    // ---------------------------------------------------------------------------------------------------------------------
    // Snapshot (main thread)

    internal static bool SinglePlayer(SimRoot sim) => sim.Game is { } game && game.GetType() == typeof(GameRoot) && RuntimeType(game.NetManager) == typeof(SPManager);
    /// <summary>Engine annotations say non-null where runtime attachment fields can be null; read the type defensively.</summary>
    private static Type? RuntimeType(object? value) => value?.GetType();

    /// <summary>
    /// Exact sim/game mode pairs whose overrides reachable from the worker path (CanDamage, CanAttack, AreEnemies/AreAllies,
    /// IsInvulnerable, IsHumanPlayerAllegiance; NativeDependencies) only read synced sim state: no local player, no writes
    /// except GetPlayerState's add of a missing player, which PlayerKnown/ExistingPlayer route to native. Any other pair runs native.
    /// </summary>
    private static readonly (Type Sim, Type Game)[] AuditedModes =
    [
        (typeof(CreativeSimModeManager), typeof(CreativeGameModeManager)), (typeof(CareerSimModeManager), typeof(CareerGameModeManager)),
        (typeof(BuildBattleSimModeManager), typeof(BuildBattleGameModeManager)), (typeof(DominationSimModeManager), typeof(DominationGameModeManager)),
        (typeof(EliminationSimModeManager), typeof(EliminationGameModeManager)), (typeof(ArenaSimModeManager), typeof(ArenaGameModeManager)),
    ];
    internal static bool AuditedMode(SimRoot sim)
    {
        if (RuntimeType(sim.Game) != typeof(GameRoot)) return false;
        Type? simMode = RuntimeType(sim.Mode), gameMode = RuntimeType(sim.Game!.Mode);
        foreach ((Type s, Type g) in AuditedModes) if (simMode == s && gameMode == g) return true;
        return false;
    }

    private static readonly HashSet<Type> AuditedModifiers =
    [
        typeof(BuffModifier), typeof(ScaledBuffModifier), typeof(BuffRemapModifier), typeof(EffectScaleModifier), typeof(ScaledEffectScaleModifier),
    ];
    /// <summary>Exact ModifiableFloat/ModifiableAngle/ModifiableTime whose modifiers only read buffs and the effect scale (no status sources).</summary>
    internal static bool SafeModifiable(object? value)
    {
        ValueModifier[]? modifiers;
        if (RuntimeType(value) == typeof(ModifiableFloat)) modifiers = ((ModifiableFloat)value!).Modifiers;
        else if (RuntimeType(value) == typeof(ModifiableAngle)) modifiers = ((ModifiableAngle)value!).Modifiers;
        else if (RuntimeType(value) == typeof(ModifiableTime)) modifiers = ((ModifiableTime)value!).Modifiers;
        else return false;
        if (modifiers == null) return true;
        foreach (ValueModifier modifier in modifiers)
            if (RuntimeType(modifier) is not { } type || !AuditedModifiers.Contains(type)) return false;
        return true;
    }

    /// <summary>
    /// GetCurrentRange, GetFireTarget/GetShipTarget and the rule values of lines 1055-1079 and the query, before any of them is
    /// called. The target getters dispatch through Emitter._getTargetFrom; only the exact FixedWeapon/TurretWeapon graph that
    /// <see cref="TargetGraphVetted"/> accepts (no lazy sight or bounding refresh, exact targets, commands and filters) is called.
    /// </summary>
    internal static bool QueryGraphVetted(BeamEmitter beam, SimRoot sim, [NotNullWhen(false)] out string? why)
    {
        BeamEmitterRules rules = beam.Rules;
        why = null;
        if (rules.StretchRange != null) why = "stretch range";
        else if (!SafeModifiable(rules.Range) || (rules.SecondaryWidth != null && !SafeModifiable(rules.SecondaryWidth)) ||
            (rules.RandomOffsetRadius is { } radius && (!SafeModifiable(radius.Min) || !SafeModifiable(radius.Max)))) why = "range modifier";
        else if (beam._getTargetFrom is { } provider)
        {
            try { why = TargetGraphVetted(beam, provider, sim); }
            finally { Array.Clear(Relevant); _relevantCount = 0; }
        }
        return why == null;
    }

    // Main thread only: ships the target graph reaches, each checked by SafeShip.
    private static readonly Ship?[] Relevant = new Ship?[36];
    private static int _relevantCount;

    /// <summary>
    /// The target getter graph of <paramref name="receiver"/> (GetShipTarget/GetFireTarget through its provider) reads only
    /// exact, cached, query-stable state; null when vetted, else a constant reason. No geometry, target, filter, modifier or
    /// custom-comparer callback is invoked here; known auto-property readers are covered by the engine file pins and the
    /// foreign-module guard.
    /// </summary>
    private static string? TargetGraphVetted(BeamEmitter receiver, Weapon provider, SimRoot sim)
    {
        _relevantCount = 0;
        bool turret = Exact<TurretWeapon>(provider);
        if (!Exact<BeamEmitter>(receiver) || !(turret || Exact<FixedWeapon>(provider)) || sim.GetType() != typeof(SimRoot)) return "target graph: provider type";
        if (!AuditedMode(sim)) return "target graph: mode";
        Guid core = typeof(object).Module.ModuleVersionId;
        if (core != new Guid("77d2994f-32f8-4ff2-8313-7ebf44feba98") && core != new Guid("47f87616-cc44-40db-91db-a39140060003")) return "target graph: runtime";
        if (!Exact<Part>(receiver.Part) || !Exact<Part>(provider.Part) ||
            receiver.Rules.GetType() != typeof(BeamEmitterRules) || provider.Rules.GetType() != (turret ? typeof(TurretWeaponRules) : typeof(FixedWeaponRules)) ||
            !Exact<BeamEmitter>(provider._targetingEmitter)) return "target graph: provider shape";
        if (turret && TurretGraph((TurretWeapon)provider) is { } turretWhy) return turretWhy;
        var targeting = (BeamEmitter)provider._targetingEmitter;
        if (!Exact<Part>(targeting.Part) || targeting.Rules.GetType() != typeof(BeamEmitterRules) ||
            !AddShip(receiver.Part.Ship) || !AddShip(provider.Part.Ship) || !AddShip(targeting.Part.Ship)) return "target graph: targeting emitter";
        Ship providerShip = provider.Part.Ship!;
        if (!Exact<WeaponManager>(providerShip.Weapons)) return "target graph: weapon manager";
        if (provider.Rules.AllowDirectControl && provider.DirectControlBinding != null && providerShip.Weapons.DirectControlInput != null) return "target graph: direct control";
        ModifiableFloat range = targeting.Rules.Range;
        if (!Exact<ModifiableFloat>(range) || range.Modifiers is { Length: > 0 } || targeting.Rules.StretchRange != null) return "target graph: targeting range";
        var coverage = providerShip.Metadata.CustomWeaponCoverages;
        if (!Exact<Dictionary<(PartSaveData, ID<PartComponentRules>), MPValue<WeaponCoverage>>>(coverage) ||
            !ReferenceEquals(coverage.Comparer, EqualityComparer<(PartSaveData, ID<PartComponentRules>)>.Default)) return "target graph: coverage";
        if (!Exact<TargetManager>(provider.ExplicitTargets)) return "target graph: target manager";
        List<WeaponTarget> targets = provider.ExplicitTargets._targets;
        if (!Exact<List<WeaponTarget>>(targets) || targets.Count > 32) return "target graph: target list";
        if (!SafeProhibitFilter(provider.Rules.ProhibitTargetPartsFilter)) return "target graph: prohibit filter";
        foreach (WeaponTarget candidate in CollectionsMarshal.AsSpan(targets))
        {
            if (!Exact<WeaponTarget>(candidate)) return "target graph: explicit target";
            if (!candidate.IsConfirmed) continue;
            if (Exact<Ship>(candidate.Target)) { if (!AddShip((Ship)candidate.Target)) return "target graph: explicit target"; }
            else if (Exact<Part>(candidate.Target))
            {
                var part = (Part)candidate.Target;
                if (!SafePart(part) || !AddShip(part.Ship)) return "target graph: explicit part";
            }
            else return "target graph: explicit target";
        }
        CommandManager commands = providerShip.Commands;
        if (!Exact<CommandManager>(commands) || !Exact<List<Command>>(commands._commands) || commands._commands.Count > 32) return "target graph: commands";
        foreach (Command command in CollectionsMarshal.AsSpan(commands._commands))
        {
            if (RuntimeType(command)?.Module.ModuleVersionId != GameMvid) return "target graph: foreign command";
            if (!command.IsConfirmed) continue;
            if (command is IAttackCommand)
            {
                if (command.GetType() != typeof(AttackCommand) || !AddShip(((AttackCommand)command)._target)) return "target graph: attack command";
            }
            break;
        }
        for (int i = 0; i < _relevantCount; i++) if (UnsafeShip(sim, Relevant[i]) is { } shipWhy) return shipWhy;
        if (sim.CurrentActionSourcePlayer is { } source && !ExistingPlayer(sim.Game, source.PlayerId, out _)) return "target graph: action source player";
        World world = sim.PhysicsWorld;
        return Exact<World>(world) && NativeWorldCallbacks(world) && Exact<ContactManager>(world.ContactManager) &&
            Exact<DynamicTreeBroadPhase<BodyFixtureProxy>>(world.ContactManager.BroadPhase) ? null : "target graph: world";
    }
    /// <summary>
    /// TurretWeapon.GetFireTarget (TurretWeapon.cs:386-401) on top of the Weapon graph vetted above: the aimed Target, then
    /// (no auto-fire toggle) the base explicit target plus IsTargetInArc. Range: GetTurretRange Valid/Normal (1248-1295)
    /// with no stretch and unmodified ValidRange / targeting emitter range, coverage clamp; arc: GetBaseArc Valid/Normal
    /// with unmodified ValidArc / FiringArc, coverage arc; HasSpecificPartInSights (Weapon.cs:1089) reads part geometry.
    /// </summary>
    private static string? TurretGraph(TurretWeapon turret)
    {
        TurretWeaponRules rules = turret.Rules;
        ITarget? aimed = turret.Target;
        if (Exact<Ship>(aimed)) { if (!AddShip((Ship)aimed)) return "target graph: turret aim"; }
        else if (Exact<Part>(aimed)) { if (!SafePart((Part)aimed) || !AddShip(((Part)aimed).Ship)) return "target graph: turret aim part"; }
        else if (aimed != null) return "target graph: turret aim";
        // IsAutoFiring reads an arbitrary toggle implementation; it is reached unless the aimed target is returned at once.
        if (turret._autoFireToggle != null && (aimed == null || rules.AllowAimAtProhibitedTarget)) return "target graph: turret auto-fire toggle";
        return Unmodified(rules.ValidRange) && Unmodified(rules.ValidArc) && Unmodified(rules.FiringArc) ? null : "target graph: turret range/arc modifier";
    }
    private static bool Unmodified(object? value) => value switch
    {
        null => true,
        ModifiableFloat f => Exact<ModifiableFloat>(f) && f.Modifiers is null or { Length: 0 },
        ModifiableAngle a => Exact<ModifiableAngle>(a) && a.Modifiers is null or { Length: 0 },
        _ => false,
    };
    private static bool Exact<T>([NotNullWhen(true)] object? value) => RuntimeType(value) == typeof(T);
    private static readonly MethodInfo NativeBroadWrapper = AccessTools.Method(typeof(World), "RayCastCallbackBroadPhaseWrapper");
    private static readonly MethodInfo NativeFixtureWrapper = AccessTools.Method(typeof(World), "RayCastCallbackFixturePhaseWrapper");
    private static bool NativeWorldCallbacks(World world) =>
        NativeCallback(world._rayCastCallbackBroadPhaseWrapper, NativeBroadWrapper, world) &&
        NativeCallback(world._rayCastCallbackFixturePhaseWrapper, NativeFixtureWrapper, world);
    private static bool AddShip(Ship? ship)
    {
        if (!Exact<Ship>(ship) || _relevantCount >= Relevant.Length) return false;
        Relevant[_relevantCount++] = ship; return true;
    }
    private static bool ExistingPlayer(GameRoot? game, int player, out int team)
    {
        team = 0;
        if (!Exact<GameRoot>(game)) return false;
        var players = game._playerStates;
        if (!Exact<SortedDictionary<int, PlayerState>>(players) || !ReferenceEquals(players.Comparer, Comparer<int>.Default) ||
            !players.TryGetValue(player, out PlayerState? state) || !Exact<PlayerState>(state)) return false;
        team = state.Team; return true;
    }
    /// <summary>Null when the ship's target-relevant caches are exact and current (no lazy sight or bounding refresh), else why not.</summary>
    private static string? UnsafeShip(SimRoot sim, Ship? ship)
    {
        if (!Exact<Ship>(ship) || !ReferenceEquals(ship._root, sim)) return "target graph: ship";
        if (ship.SpecialSightProvider != null) return "target graph: special sight";
        if (!Exact<PartsManager>(ship.Parts) || !Exact<ShipMetadata>(ship.Metadata)) return "target graph: ship shape";
        if (ship.Parts._isBoundingCircleDirty || !ship.Parts._cachedBoundingCircle.HasValue) return "target graph: bounding circle dirty";
        if (!ExistingPlayer(sim.Game, ship.Metadata.PlayerIndex, out int team)) return "target graph: player state";
        var manager = sim.Ships;
        if (!Exact<SimShipsManager>(manager)) return "target graph: sight cache stale";
        var sight = manager._detSightCirclesByTeam;
        return Exact<ConcurrentDictionary<int, SimShipsManager.TeamSightCircles>>(sight) &&
            ReferenceEquals(sight.Comparer, EqualityComparer<int>.Default) && sight.TryGetValue(team, out var cache) &&
            Exact<SimShipsManager.TeamSightCircles>(cache) && cache.RefreshNum == manager._sightCircleRefreshBuster ? null : "target graph: sight cache stale";
    }
    private static bool SafeProhibitFilter(EffectFilter? filter) => filter == null || (filter.GetType() == typeof(EffectFilter) &&
        filter.OnlyStatuses is null or { Length: 0 } && filter.ExcludeStatuses is null or { Length: 0 } &&
        SafeSet(filter.OnlyPartCategories) && SafeSet(filter.ExcludePartCategories) &&
        filter.IgnoreInvulnerability && filter.Friendlies.HasValue && filter.Enemies.HasValue && filter.Neutrals.HasValue && filter.Junk.HasValue);
    private static bool SafePart([NotNullWhen(true)] Part? part) => Exact<Part>(part) && Exact<PartRules>(part.Rules) &&
        Exact<HashSet<ID<PartCategory>>>(part.Rules.TypeCategories) && SafeSet(part.Rules.TypeCategories);

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

    private static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    /// <summary>A worker result against the native one, bit for bit, including the structural hit order (null when equal).</summary>
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
    private static bool Same(V a, V b) => Same(a.X, b.X) && Same(a.Y, b.Y);

    // ---------------------------------------------------------------------------------------------------------------------
    // Worker query (any thread). Reads frozen state only; writes only its record and its own thread's context.

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
}
