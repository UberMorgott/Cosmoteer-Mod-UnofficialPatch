using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using Cosmoteer;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Modes.Creative;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Blueprints;
using Cosmoteer.Ships.Commands;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Weapons;
using Cosmoteer.Simulation;
using Cosmoteer.Simulation.HitEffects;
using Halfling;
using Halfling.Application;
using Halfling.Physics2D.Collision;
using Halfling.Physics2D.Collision.Shapes;
using Halfling.Physics2D.Dynamics;
using HarmonyLib;

namespace Performance;

internal static class BeamTargetMemo
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_BEAM_TARGET_MEMO"));
    internal static readonly bool VerificationRequested = Requested && IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_BEAM_TARGET_VERIFY"));
    internal static bool IsRequested(string? value) => value == "1";
    private static int _ready;
    private static bool _attempted;
    private static string? _failureReason;
    private static Director? _director;
    private static WeakReference<SimRoot>? _owner;
    private static int _thread;
    private static Definition[]? _definitions;
    private static RequiredHook[]? _requiredHooks;
    private static KnownLoaderPatches.Registration[]? _installedHooks;
    private readonly record struct RequiredHook(MethodBase Target, KnownLoaderPatches.Kind Kind, MethodInfo Method);
    private static readonly Guid GameMvid = new("fd7c0bba-64d3-40b4-a571-bf8b34eb4277");
    private static readonly Guid HalflingMvid = new("29d04acb-ae49-4f3b-8499-f03c9144a3a9");
    private static readonly MethodInfo Getter = AccessTools.Method(typeof(Emitter), "GetShipTarget");
    private static readonly MethodInfo SecondaryCallback = AccessTools.Method(typeof(BeamEmitter), "ValidateSecondaryHit");
    private static readonly MethodInfo NativePartFilter = AccessTools.Method(typeof(EffectFilter), "ValidateHitPart");
    private static readonly MethodInfo NativeBroadWrapper = AccessTools.Method(typeof(World), "RayCastCallbackBroadPhaseWrapper");
    private static readonly MethodInfo NativeFixtureWrapper = AccessTools.Method(typeof(World), "RayCastCallbackFixturePhaseWrapper");
    private static readonly MethodBase DataWorldCallback = typeof(PhysicsExtensions).Module.ResolveMethod(0x06005C05)!;
    private static readonly Func<Emitter, bool, Ship?> Native = Getter.CreateDelegate<Func<Emitter, bool, Ship?>>();
    private static long _hits, _misses, _nativeCalls, _invalidations, _eligiblePureFills, _verificationComparisons, _verificationMismatches;
    [ThreadStatic] private static WeaponManager? _emitting;
    [ThreadStatic] private static Query? _query;
    [ThreadStatic] private static Query? _pool;
    [ThreadStatic] private static int _worldDepth;
    internal static bool Ready => Volatile.Read(ref _ready) != 0;
    internal static string? FailureReason => Volatile.Read(ref _failureReason);
    internal static long Hits => Interlocked.Read(ref _hits);
    internal static long Misses => Interlocked.Read(ref _misses);
    internal static long NativeCalls => Interlocked.Read(ref _nativeCalls);
    internal static long Invalidations => Interlocked.Read(ref _invalidations);
    internal static long EligiblePureFills => Interlocked.Read(ref _eligiblePureFills);
    internal static long VerificationComparisons => Interlocked.Read(ref _verificationComparisons);
    internal static long VerificationMismatches => Interlocked.Read(ref _verificationMismatches);
    internal static (MethodBase Target, Type Patch)[] Patches => Requested ?
        (_definitions ??= ReadDefinitions()).Select(d => ((MethodBase)d.Target, d.Patch)).ToArray() : [];

    internal sealed record Definition(MethodInfo Target, string Hash, int Calls, Type Patch);
    internal static Definition[] ReadDefinitions()
    {
        MethodInfo M(Type type, string name) => AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);
        MethodInfo Token(Type type, int token) => (MethodInfo)type.Module.ResolveMethod(token)!;
        return
        [
            new(M(typeof(WeaponManager), "EmitBeams"), "593A1D02E3BCAF1B749E835918D516604DCCB6180428F95FBE6BF86171B5FA33", 0, typeof(EmitHook)),
            new(M(typeof(BeamEmitter), "DoSecondaryRaycast"), "EE103602DDE7B3DA1912B04938EDB9671FF2165B82BEBF3908558E6BBE8FCF56", 0, typeof(SecondaryHook)),
            new(M(typeof(BeamEmitter), "IsShipHitValid"), "BFF2084ACC5703FB0F0110E8FD7FF522A2BA3E98DB695B2670A46BF95D6A32C0", 1, typeof(CallHook)),
            new(SecondaryCallback, "8268EC1094B51ECFFE08084D52B20C7BF239C00A5ED34100A04BC847DFA91DE3", 2, typeof(FilterHook)),
            new(M(typeof(BeamEmitter), "<IsShipHitValid>g___IsFixtureLow|89_0"), "3ED9C7E52C7370444C7D9DB513250667056BA76352C39A57CB35006479576028", 0, typeof(CallHook)),
            new(M(typeof(BeamEmitter), "<IsShipHitValid>g___IsFixtureHigh|89_1"), "6922D27E3F91375A9052FCB02605E049BC8DEF7947DEEA910D7F156C16AD1B11", 0, typeof(CallHook)),
            new(M(typeof(BeamEmitter), "<IsShipHitValid>g___IsStructural|89_2"), "6B95DBC41DB93261A9D425432AE0359DC5E0A61095625C060AFE9E25EB4E6447", 0, typeof(CallHook)),
            new(M(typeof(BeamEmitter), "<IsShipHitValid>g___CanDamageShip|89_3"), "AD131920380924F42F9C3BB0CBA87EEE1CA0B829F94B93D83C07C577455EF1D8", 0, typeof(CallHook)),
            new(M(typeof(BeamEmitter), "<IsShipHitValid>g___GetNonSourceShipTarget|89_4"), "5F4112220F651BD1E9DD0CFB55D09AF1EFB77E001601699EC4E608EB5F4A6769", 1, typeof(CallHook)),
            new(M(typeof(BeamEmitter), "<ValidateSecondaryHit>g___GetSourcePlayerIndex|90_0"), "4B1B28225BC6BF59F47A7B60A460E7753D3DD1FF4B6CA9441912E6B9C03B0DB1", 0, typeof(CallHook)),
            new(M(typeof(BeamEmitter), "<ValidateSecondaryHit>g___FilterHitShip|90_1"), "EB90802ADFAE9929C471930F7DC82C8035E7BF65E3F3AE6EC9BCEDC195DD71E8", 1, typeof(CallHook)),
            new(M(typeof(BeamEmitter), "<ValidateSecondaryHit>g___FilterHitPart|90_2"), "92F4DB51A097DC9448554D42DE71243C6D938449D4971B824FD5A7223A28B414", 0, typeof(CallHook)),
            new(M(typeof(World), "RayCastCallbackBroadPhaseWrapper"), "92EBF94E76439327AA85B98BCC259562E3DBB1EFF61ADC70D39705971836C2CE", 0, typeof(BroadHook)),
            new(M(typeof(World), "RayCastCallbackFixturePhaseWrapper"), "322858FCA2E819C38321EE908487561D4150B60128F4CA31BDFF3F3B4668FC31", 0, typeof(FixtureHook)),
            new(M(typeof(World), "QueryAABB"), "19BA7FE3DE2D02317A7E01ADB8808F35D20696D0A732A70BD8BF3127C48C8D46", 0, typeof(WorldHook)),
            new(M(typeof(World), "RayCast"), "D408B9B8BC2FB29C43FDFEA0C76729017D9B034821C41D697B5BA1B12F48DF74", 0, typeof(WorldRayHook)),
            new(M(typeof(World), "TestPoint"), "CF4ED05AAFEF38F26D5F489C2BDFB93CC4F4F496D927DD19AEED128E7ECB6F70", 0, typeof(WorldHook)),
            new(Token(typeof(World), 0x06000D89), "B7243D69C6EB879C33C488B6E17F02F22E7AE042C80EE5FBF195908D4616F687", 0, typeof(WorldHook)),
            new(Token(typeof(World), 0x06000D8A), "13D15067B4CCFFE93C1ADE81D602A4599896852E809C604493E5DE82E7DB98AC", 0, typeof(WorldHook)),
            new(Token(typeof(World), 0x060010F6), "9C81C9BAE3A1027F53ED2684A03E239C53F846E8DE0F522C82DB673289C3E9B2", 0, typeof(CallHook)),
            new(Token(typeof(World), 0x06001130), "75791869D8B8E036BBADDEB466F8C84A5766CB8863F8F6BE6FD3F7B4D9A84130", 0, typeof(CallHook)),
        ];
    }
    internal static void ValidateDefinition(Definition definition)
    {
        MethodInfo method = definition.Target;
        Guid expected = method.Module == typeof(World).Module ? HalflingMvid : GameMvid;
        if (method.ContainsGenericParameters || method.Module.ModuleVersionId != expected ||
            method.GetMethodBody()?.GetILAsByteArray() is not { } il || Convert.ToHexString(SHA256.HashData(il)) != definition.Hash)
            throw new NotSupportedException("Beam target memo native IL/MVID changed: " + method);
    }

    internal static bool Initialize()
    {
        if (!Requested) return true;
        if (_attempted) return Ready;
        _attempted = true;
        try
        {
            Director director = App.Director ?? throw new NotSupportedException("Beam target memo requires native Director");
            if (director.FrameCount != 0) throw new NotSupportedException("Beam target memo requires startup FrameCount == 0");
            PinFile(typeof(BeamEmitter).Assembly, "F9451FBED4A11396C816D3F44EBDD836E48BCE68BA6AEAE28C3E534E037207D4");
            PinFile(typeof(World).Assembly, "80213C7C8182CE63B9075A48DFD80A7614B1FA2AB3BCD16A69D5363CF9A4B391");
            _definitions ??= ReadDefinitions();
            foreach (Definition definition in _definitions)
            {
                ValidateDefinition(definition);
                foreach (MethodBase parent in Patcher.FindR2RInliners(definition.Target))
                    if (!_definitions.Any(d => Same(d.Target, parent))) throw new NotSupportedException("Uncovered precompiled beam memo caller: " + parent);
                if (!Patcher.TryDisableInlining(definition.Target, out string? error)) throw new NotSupportedException(error);
            }
            if (!Compatible(out string? compatibilityError)) throw new NotSupportedException(compatibilityError);
            _director = director; director.FrameStarting += OnFrameStarting;
            Volatile.Write(ref _ready, 1);
            Log.Info("Beam target memo prototype ready: explicit owned SP EmitBeams binding required");
            return true;
        }
        catch (Exception ex) { Reject(ex.Message); return false; }
    }
    private static void PinFile(Assembly assembly, string expected)
    {
        using FileStream input = File.OpenRead(assembly.Location);
        if (Convert.ToHexString(SHA256.HashData(input)) != expected) throw new NotSupportedException("Beam memo native dependency file changed: " + assembly.GetName().Name);
    }
    private static bool Same(MethodBase a, MethodBase b) => a.Module == b.Module && a.MetadataToken == b.MetadataToken;
    private static RequiredHook[] ReadRequiredHooks()
    {
        var result = new List<RequiredHook>();
        foreach (Definition d in _definitions ?? [])
        {
            foreach (var hook in new[] { ("Transpiler", KnownLoaderPatches.Kind.Transpiler), ("Prefix", KnownLoaderPatches.Kind.Prefix), ("Finalizer", KnownLoaderPatches.Kind.Finalizer) })
            {
                if (d.Patch.GetMethod(hook.Item1, BindingFlags.Public | BindingFlags.Static) is { } method) result.Add(new(d.Target, hook.Item2, method));
                else if (hook.Item2 == KnownLoaderPatches.Kind.Transpiler) throw new NotSupportedException("Beam memo transpiler definition missing: " + d.Target);
            }
        }
        return result.ToArray();
    }
    private static void ValidateHooks(KnownLoaderPatches.Registration[] registrations)
    {
        RequiredHook[] required = _requiredHooks ??= ReadRequiredHooks();
        KnownLoaderPatches.Registration[]? initial = _installedHooks == null ? new KnownLoaderPatches.Registration[required.Length] : null;
        for (int i = 0; i < required.Length; i++)
        {
            RequiredHook wanted = required[i];
            KnownLoaderPatches.Registration found = default; int count = 0;
            foreach (var registration in registrations)
                if (Same(registration.Target, wanted.Target) && registration.Owner == Patcher.HarmonyId && registration.HookKind == wanted.Kind && registration.Hook.Equals(wanted.Method))
                { found = registration; count++; }
            if (count != 1) throw new NotSupportedException("Beam memo hook coverage missing/duplicate: " + wanted.Target);
            if (initial != null) initial[i] = found with { Before = found.Before == null ? null : (string[])found.Before.Clone(), After = found.After == null ? null : (string[])found.After.Clone() };
            else
            {
                var installed = _installedHooks![i];
                if (installed.Index != found.Index || installed.Priority != found.Priority || installed.Debug != found.Debug || installed.InnerMethod != found.InnerMethod ||
                    !(installed.Before ?? []).AsSpan().SequenceEqual(found.Before ?? []) || !(installed.After ?? []).AsSpan().SequenceEqual(found.After ?? []))
                    throw new NotSupportedException("Beam memo installed hook ordering changed: " + wanted.Target);
            }
        }
        if (initial != null) _installedHooks = initial;
    }
    // The exact complete loader inventory is the only foreign exception. Shared generic definitions in all protected
    // modules retain the existing conservative guard; no generic engine method is detoured here.
    private static bool Compatible(out string? error)
    {
        try
        {
            var view = HarmonyPatchIdentity.ReadRuntimeView();
            var registrations = view.Registrations;
            ValidateHooks(registrations);
            var loader = KnownLoaderPatches.Validate(registrations, view.LoaderModules);
            foreach (var registration in registrations)
            {
                Module module = registration.Target.Module;
                if (module != typeof(BeamEmitter).Module && module != typeof(World).Module && module != typeof(object).Module &&
                    module != typeof(SortedDictionary<,>).Module && module != typeof(ConcurrentDictionary<,>).Module) continue;
                if (registration.Owner == Patcher.HarmonyId || loader.Allows(registration)) continue;
                error = "Foreign patch on beam target memo dependency: " + KnownLoaderPatches.Describe(registration);
                return false;
            }
            error = null; return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
    internal static void CheckCompatibility()
    {
        if (!Ready) return;
        try { if (!Compatible(out string? error)) Reject(error ?? "Beam target memo compatibility unavailable"); }
        catch (Exception ex) { Reject(ex.Message); }
    }
    private static void OnFrameStarting(object? sender, EventArgs e) => CheckCompatibility();
    internal static bool BindOwnedSimulation(SimRoot sim)
    {
        if (!Requested || !Ready || !SinglePlayer(sim)) return false;
        if (!ReferenceEquals(_director?.MainThread, Thread.CurrentThread) || !ReferenceEquals(sim.SceneThread, Thread.CurrentThread)) return false;
        if (_owner != null && _owner.TryGetTarget(out SimRoot? current)) return ReferenceEquals(current, sim);
        if (_query != null || _emitting != null) return false;
        _owner = new WeakReference<SimRoot>(sim); _thread = Environment.CurrentManagedThreadId;
        return true;
    }
    internal static void Disable()
    {
        Volatile.Write(ref _ready, 0); Volatile.Write(ref _owner, null); InvalidateAll();
        if (_director == null) return;
        _director.FrameStarting -= OnFrameStarting; _director = null;
        // Do not reset TLS scopes: their finalizers must unwind even after disarm.
    }
    private static void Reject(string reason)
    {
        if (Interlocked.CompareExchange(ref _failureReason, reason, null) == null) Log.Info("Beam target memo disabled: " + reason);
        Disable();
    }
    private static bool Owned(SimRoot? sim) => Ready && Environment.CurrentManagedThreadId == _thread && sim != null &&
        ReferenceEquals(_director?.MainThread, Thread.CurrentThread) && ReferenceEquals(sim.SceneThread, Thread.CurrentThread) &&
        !sim.IsDoingParallelUpdate && SinglePlayer(sim) &&
        Volatile.Read(ref _owner) is { } reference && reference.TryGetTarget(out SimRoot? owner) && ReferenceEquals(owner, sim);
    // Runtime attachment fields can be null despite the engine's nullable annotations.
    private static Type? RuntimeType(object? value) => value?.GetType();
    private static bool Exact<T>([NotNullWhen(true)] object? value) => RuntimeType(value) == typeof(T);
    private static bool SinglePlayer(SimRoot sim)
    {
        GameRoot? game = sim.Game;
        return RuntimeType(game) != null && Exact<SPManager>(game!.NetManager);
    }

    internal struct Entry
    {
        private Emitter? _receiver;
        private Weapon? _provider;
        private Ship? _result;
        private bool _argument, _hasValue, _invalid;
        internal bool Invalid => _invalid;
        internal void Store(Emitter receiver, Weapon provider, bool argument, Ship? result)
        {
            if (_invalid) return;
            _receiver = receiver; _provider = provider; _argument = argument; _result = result; _hasValue = true;
        }
        internal bool TryGet(Emitter receiver, Weapon provider, bool argument, out Ship? result)
        {
            result = _result;
            return !_invalid && _hasValue && ReferenceEquals(receiver, _receiver) && ReferenceEquals(provider, _provider) && argument == _argument;
        }
        internal void Invalidate() { _invalid = true; _receiver = null; _provider = null; _result = null; _hasValue = false; }
    }
    internal sealed class Query
    {
        internal Query? Parent, Next;
        internal BeamEmitter? Receiver;
        internal Weapon? Provider;
        internal SimRoot? Sim;
        internal World? World;
        internal Entry Value;
        internal bool ExpectWorld, InWorld, Calling;
        internal readonly Ship?[] RelevantShips = new Ship?[36];
        internal int RelevantCount;
        internal void Clear()
        {
            Parent = null; Next = null; Receiver = null; Provider = null; Sim = null; World = null; Value = default;
            ExpectWorld = false; InWorld = false; Calling = false;
            Array.Clear(RelevantShips); RelevantCount = 0;
        }
    }
    internal static void InvalidateAll()
    {
        for (Query? q = _query; q != null; q = q.Parent)
        {
            if (!q.Value.Invalid) _invalidations++;
            q.Value.Invalidate();
        }
    }
    private static Ship? Lookup(Emitter receiver, bool argument) => Invoke(receiver, argument, Native);
    internal static Ship? Invoke(Emitter receiver, bool argument, Func<Emitter, bool, Ship?> native)
    {
        Query? q = _query;
        if (q == null) return native(receiver, argument);
        if (RuntimeType(receiver) == null) { InvalidateAll(); return native(receiver, argument); }
        Weapon? provider = receiver._getTargetFrom;
        bool usable = Ready && q.InWorld && !q.Calling && !q.Value.Invalid && ReferenceEquals(q.Receiver, receiver) &&
            provider != null && ReferenceEquals(q.Provider, provider);
        if (!usable) InvalidateAll();
        if (usable && q.Value.TryGet(receiver, provider!, argument, out Ship? cached))
        {
            _hits++;
            if (!VerificationRequested) return cached;
            _nativeCalls++; _verificationComparisons++;
            Ship? actual;
            q.Calling = true;
            try { actual = native(receiver, argument); }
            catch { Reject("Beam memo verification native exception"); throw; }
            finally { q.Calling = false; }
            if (!ReferenceEquals(cached, actual)) { _verificationMismatches++; Reject("Beam memo verification reference mismatch"); }
            return actual;
        }
        if (usable)
        {
            try { if (!PureBefore(q)) { InvalidateAll(); usable = false; } }
            catch { InvalidateAll(); usable = false; }
        }
        _misses++; _nativeCalls++;
        q.Calling = true;
        Ship? result;
        try { result = native(receiver, argument); }
        catch { Reject("Beam memo native exception"); throw; }
        finally { q.Calling = false; }
        if (usable && Ready && !q.Value.Invalid && ReferenceEquals(_query, q) && ReferenceEquals(receiver._getTargetFrom, provider))
        {
            q.Value.Store(receiver, provider!, argument, result); _eligiblePureFills++;
        }
        else InvalidateAll();
        return result;
    }

    private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        Definition definition = (_definitions ??= ReadDefinitions()).Single(d => Same(d.Target, original));
        ValidateDefinition(definition);
        var code = instructions.ToList(); var native = PatchProcessor.GetOriginalInstructions(original);
        int Target(List<CodeInstruction> body, Label label) => body.FindIndex(i => i.labels.Contains(label));
        bool Operand(object? a, object? b)
        {
            if (a is Label x && b is Label y) return Target(code, x) >= 0 && Target(code, x) == Target(native, y);
            if (a is Label[] xx && b is Label[] yy) return xx.Length == yy.Length && xx.Select(branch => Target(code, branch)).SequenceEqual(yy.Select(branch => Target(native, branch)));
            if (a is LocalBuilder lx && b is LocalBuilder ly) return lx.LocalIndex == ly.LocalIndex && lx.LocalType == ly.LocalType;
            return Equals(a, b);
        }
        if (code.Count != native.Count || code.Where((instruction, i) => instruction.opcode != native[i].opcode || !Operand(instruction.operand, native[i].operand) ||
            !instruction.blocks.Select(b => (b.blockType, b.catchType)).SequenceEqual(native[i].blocks.Select(b => (b.blockType, b.catchType)))).Any())
            throw new NotSupportedException("Beam memo incoming IL changed: " + original);
        CodeInstruction[] calls = code.Where(i => i.Calls(Getter)).ToArray();
        if (calls.Length != definition.Calls || calls.Any(call => call.opcode != OpCodes.Call)) throw new NotSupportedException("Beam memo getter call shape changed: " + original);
        foreach (CodeInstruction call in calls) call.operand = AccessTools.Method(typeof(BeamTargetMemo), nameof(Lookup));
        if (definition.Target.Name == "<ValidateSecondaryHit>g___FilterHitPart|90_2")
        {
            CodeInstruction[] filters = code.Where(i => i.Calls(NativePartFilter)).ToArray();
            if (filters.Length != 1 || filters[0].opcode != OpCodes.Callvirt) throw new NotSupportedException("Beam memo part-filter call shape changed");
            filters[0].opcode = OpCodes.Call;
            filters[0].operand = AccessTools.Method(typeof(BeamTargetMemo), nameof(FilterPart));
        }
        return code;
    }
    internal static class CallHook
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) => Rewrite(instructions, __originalMethod);
    }
    internal static class EmitHook
    {
        public static void Prefix(WeaponManager __instance, out WeaponManager? __state)
        {
            __state = _emitting;
            if (_emitting != null) InvalidateAll();
            _emitting = null;
            try { if (Owned(__instance.Sim)) _emitting = __instance; }
            catch { InvalidateAll(); }
        }
        public static Exception? Finalizer(WeaponManager? __state, Exception? __exception) { _emitting = __state; return __exception; }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) => Rewrite(instructions, __originalMethod);
    }
    internal static class SecondaryHook
    {
        public static void Prefix(BeamEmitter __instance, out Query? __state)
        {
            __state = null;
            bool nested = _query != null;
            if (nested) InvalidateAll();
            try { if (!nested && (_emitting == null || !Owned(__instance.Sim) || !ReferenceEquals(_emitting.Ship, __instance.Ship))) return; }
            catch { InvalidateAll(); return; }
            Query q = _pool ?? new Query(); _pool = q.Next; q.Next = null;
            q.Parent = _query; q.Receiver = __instance; q.Provider = __instance._getTargetFrom;
            q.Sim = __instance.Sim; q.World = q.Sim?.PhysicsWorld; q.ExpectWorld = true;
            _query = q; __state = q;
            try { if (nested || !Ready || !PureBefore(q)) q.Value.Invalidate(); }
            catch { q.Value.Invalidate(); }
        }
        public static Exception? Finalizer(Query? __state, Exception? __exception)
        {
            if (__state == null) return __exception;
            if (!ReferenceEquals(_query, __state)) { InvalidateAll(); return __exception; }
            _query = __state.Parent; __state.Clear(); __state.Next = _pool; _pool = __state;
            return __exception;
        }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) => Rewrite(instructions, __originalMethod);
    }
    internal static class WorldHook
    {
        public static void Prefix(out int __state) { __state = _worldDepth; if (_query != null) InvalidateAll(); _worldDepth++; }
        public static Exception? Finalizer(int __state, Exception? __exception) { _worldDepth = __state; return __exception; }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) => Rewrite(instructions, __originalMethod);
    }
    internal static class WorldRayHook
    {
        public static void Prefix(World __instance, Func<Fixture, System.Numerics.Vector2, System.Numerics.Vector2, float, float> callback, out int __state)
        {
            __state = _worldDepth; _worldDepth++;
            Query? q = _query; if (q == null) return;
            bool expected = false;
            try
            {
                expected = q.ExpectWorld && __state == 0 && ReferenceEquals(q.World, __instance) && __instance.GetType() == typeof(World) &&
                    NativeWorldCallbacks(__instance) &&
                    callback is { HasSingleTarget: true } && Same(callback.Method, DataWorldCallback) &&
                    PhysicsExtensions.s_dataCondition is { HasSingleTarget: true } condition &&
                    ReferenceEquals(condition.Target, q.Receiver) && Same(condition.Method, SecondaryCallback);
            }
            catch { InvalidateAll(); }
            q.ExpectWorld = false; q.InWorld = expected;
            if (!expected) InvalidateAll();
        }
        public static Exception? Finalizer(int __state, Exception? __exception)
        {
            if (_query != null && __state == 0) _query.InWorld = false;
            _worldDepth = __state; return __exception;
        }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) => Rewrite(instructions, __originalMethod);
    }
    internal static class BroadHook
    {
        public static void Prefix(World __instance, int proxyId)
        {
            if (_query == null || _query.Value.Invalid) return;
            try
            {
                if (__instance.ContactManager.BroadPhase.GetType() != typeof(DynamicTreeBroadPhase<BodyFixtureProxy>)) { InvalidateAll(); return; }
                BodyFixtureProxy proxy = __instance.ContactManager.BroadPhase.GetProxy(proxyId);
                if (proxy.Fixture != null) InspectFixture(proxy.Fixture);
                else if (!Exact<Body>(proxy.Body) || !Exact<Ship>(proxy.Body.UserData) ||
                    !Exact<DynamicTree<BodyFixtureProxy>>(proxy.Body.FixtureTree) || !NativeWorldCallbacks(__instance)) InvalidateAll();
            }
            catch { InvalidateAll(); }
        }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) => Rewrite(instructions, __originalMethod);
    }
    internal static class FixtureHook
    {
        public static void Prefix(int proxyId, object userData)
        {
            if (_query == null || _query.Value.Invalid) return;
            try
            {
                if (!Exact<Body>(userData) || !Exact<DynamicTree<BodyFixtureProxy>>(((Body)userData).FixtureTree)) { InvalidateAll(); return; }
                InspectFixture(((Body)userData).FixtureTree!.GetUserData(proxyId).Fixture);
            }
            catch { InvalidateAll(); }
        }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) => Rewrite(instructions, __originalMethod);
    }
    internal static class FilterHook
    {
        public static void Prefix(BeamEmitter __instance, Fixture fixture)
        {
            if (_query == null) return;
            if (!ReferenceEquals(_query.Receiver, __instance)) { InvalidateAll(); return; }
            InspectFixture(fixture);
        }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) => Rewrite(instructions, __originalMethod);
    }

    // No geometry, target, filter, modifier or custom-comparer callback is invoked by the pre-call guard.
    // Known auto-property readers below are covered by the exact engine file pins and foreign-module guard.
    internal static bool PureBefore(Query q) => PureBefore(q, secondaryFilter: true);
    /// <param name="q">The query whose receiver, provider, sim and world are checked.</param>
    /// <param name="secondaryFilter">False: vet only the target getter graph (GetShipTarget/GetFireTarget), not the secondary filter callback.</param>
    internal static bool PureBefore(Query q, bool secondaryFilter)
    {
        q.RelevantCount = 0;
        BeamEmitter? receiver = q.Receiver;
        if (!Exact<BeamEmitter>(receiver) || !Exact<FixedWeapon>(q.Provider) || q.Sim == null ||
            !ReferenceEquals(receiver._getTargetFrom, q.Provider) || q.Sim.GetType() != typeof(SimRoot) ||
            !Exact<GameRoot>(q.Sim.Game) || !Exact<CreativeSimModeManager>(q.Sim.Mode) ||
            !Exact<CreativeGameModeManager>(q.Sim.Game.Mode)) return false;
        Guid core = typeof(object).Module.ModuleVersionId;
        if (core != new Guid("77d2994f-32f8-4ff2-8313-7ebf44feba98") && core != new Guid("47f87616-cc44-40db-91db-a39140060003")) return false;
        var provider = (FixedWeapon)q.Provider;
        if (!Exact<Part>(receiver.Part) || !Exact<Part>(provider.Part) ||
            receiver.Rules.GetType() != typeof(BeamEmitterRules) || provider.Rules.GetType() != typeof(FixedWeaponRules) ||
            !Exact<BeamEmitter>(provider._targetingEmitter)) return false;
        var targeting = (BeamEmitter)provider._targetingEmitter;
        if (!Exact<Part>(targeting.Part) || targeting.Rules.GetType() != typeof(BeamEmitterRules) ||
            !AddShip(q, receiver.Part.Ship) || !AddShip(q, provider.Part.Ship) || !AddShip(q, targeting.Part.Ship)) return false;
        Ship providerShip = provider.Part.Ship!;
        if (!Exact<WeaponManager>(providerShip.Weapons)) return false;
        if (provider.Rules.AllowDirectControl && provider.DirectControlBinding != null && providerShip.Weapons.DirectControlInput != null) return false;
        ModifiableFloat range = targeting.Rules.Range;
        if (!Exact<ModifiableFloat>(range) || range.Modifiers is { Length: > 0 } || targeting.Rules.StretchRange != null) return false;
        var coverage = providerShip.Metadata.CustomWeaponCoverages;
        if (!Exact<Dictionary<(PartSaveData, ID<PartComponentRules>), MPValue<WeaponCoverage>>>(coverage) ||
            !ReferenceEquals(coverage.Comparer, EqualityComparer<(PartSaveData, ID<PartComponentRules>)>.Default)) return false;
        if (!Exact<TargetManager>(provider.ExplicitTargets)) return false;
        List<WeaponTarget> targets = provider.ExplicitTargets._targets;
        if (!Exact<List<WeaponTarget>>(targets) || targets.Count > 32) return false;
        if (!SafeFilter(provider.Rules.ProhibitTargetPartsFilter) || (secondaryFilter && !SafeScopedFilter(q, receiver.Rules.SecondaryFilter))) return false;
        foreach (WeaponTarget candidate in CollectionsMarshal.AsSpan(targets))
        {
            if (!Exact<WeaponTarget>(candidate)) return false;
            if (!candidate.IsConfirmed) continue;
            if (Exact<Ship>(candidate.Target)) { if (!AddShip(q, (Ship)candidate.Target)) return false; }
            else if (Exact<Part>(candidate.Target))
            {
                var part = (Part)candidate.Target;
                if (!SafePart(part) || !AddShip(q, part.Ship)) return false;
            }
            else return false;
        }
        CommandManager commands = providerShip.Commands;
        if (!Exact<CommandManager>(commands) || !Exact<List<Command>>(commands._commands) || commands._commands.Count > 32) return false;
        foreach (Command command in CollectionsMarshal.AsSpan(commands._commands))
        {
            if (RuntimeType(command)?.Module.ModuleVersionId != GameMvid) return false;
            if (!command.IsConfirmed) continue;
            if (command is IAttackCommand)
            {
                if (command.GetType() != typeof(AttackCommand) || !AddShip(q, ((AttackCommand)command)._target)) return false;
            }
            break;
        }
        for (int i = 0; i < q.RelevantCount; i++) if (!SafeShip(q, q.RelevantShips[i])) return false;
        if (q.Sim.CurrentActionSourcePlayer is { } source && !ExistingPlayer(q.Sim.Game, source.PlayerId, out _)) return false;
        World? world = q.World;
        return Exact<World>(world) && ReferenceEquals(world, q.Sim.PhysicsWorld) && NativeWorldCallbacks(world) && Exact<ContactManager>(world.ContactManager) &&
            Exact<DynamicTreeBroadPhase<BodyFixtureProxy>>(world.ContactManager.BroadPhase);
    }
    private static bool NativeWorldCallbacks(World world) =>
        NativeCallback(world._rayCastCallbackBroadPhaseWrapper, NativeBroadWrapper, world) &&
        NativeCallback(world._rayCastCallbackFixturePhaseWrapper, NativeFixtureWrapper, world);
    private static bool NativeCallback(Delegate? callback, MethodBase method, object target) => callback is { HasSingleTarget: true } &&
        ReferenceEquals(callback.Target, target) && Same(callback.Method, method);
    private static bool AddShip(Query q, Ship? ship)
    {
        if (!Exact<Ship>(ship) || q.RelevantCount >= q.RelevantShips.Length) return false;
        q.RelevantShips[q.RelevantCount++] = ship; return true;
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
    private static bool SafeShip(Query q, Ship? ship)
    {
        if (!Exact<Ship>(ship) || !ReferenceEquals(ship._root, q.Sim) || ship.SpecialSightProvider != null ||
            !Exact<PartsManager>(ship.Parts) || !Exact<ShipMetadata>(ship.Metadata) ||
            ship.Parts._isBoundingCircleDirty || !ship.Parts._cachedBoundingCircle.HasValue ||
            !ExistingPlayer(q.Sim!.Game, ship.Metadata.PlayerIndex, out int team)) return false;
        var manager = q.Sim.Ships;
        if (!Exact<SimShipsManager>(manager)) return false;
        var sight = manager._detSightCirclesByTeam;
        return Exact<ConcurrentDictionary<int, SimShipsManager.TeamSightCircles>>(sight) &&
            ReferenceEquals(sight.Comparer, EqualityComparer<int>.Default) && sight.TryGetValue(team, out var cache) &&
            Exact<SimShipsManager.TeamSightCircles>(cache) && cache.RefreshNum == manager._sightCircleRefreshBuster;
    }
    private static bool SafeSet(HashSet<ID<PartCategory>>? set) => set == null ||
        (set.GetType() == typeof(HashSet<ID<PartCategory>>) && ReferenceEquals(set.Comparer, EqualityComparer<ID<PartCategory>>.Default));
    private static bool SafeFilter([NotNullWhen(false)] EffectFilter? filter) => filter == null || (SafeFilterShape(filter) &&
        filter.IgnoreInvulnerability && filter.Friendlies.HasValue && filter.Enemies.HasValue && filter.Neutrals.HasValue && filter.Junk.HasValue);
    private static bool SafeFilterShape(EffectFilter filter) => filter.GetType() == typeof(EffectFilter) &&
        filter.OnlyStatuses is null or { Length: 0 } && filter.ExcludeStatuses is null or { Length: 0 } &&
        SafeSet(filter.OnlyPartCategories) && SafeSet(filter.ExcludePartCategories);
    private static bool SafeScopedFilter(Query q, EffectFilter? filter)
    {
        if (SafeFilter(filter)) return true;
        BeamEmitter? receiver = q.Receiver;
        // Only this live secondary filter may use the native Creative invulnerability/allegiance closure.
        // Its player lookups are read-only only while every referenced player already exists.
        return Ready && !q.Value.Invalid && ReferenceEquals(_query, q) && Exact<BeamEmitter>(receiver) &&
            ReferenceEquals(receiver._getTargetFrom, q.Provider) &&
            ReferenceEquals(filter, receiver.Rules.SecondaryFilter) && SafeFilterShape(filter) &&
            Exact<SimRoot>(q.Sim) && Owned(q.Sim) && Exact<GameRoot>(q.Sim.Game) &&
            Exact<CreativeSimModeManager>(q.Sim.Mode) && Exact<CreativeGameModeManager>(q.Sim.Game.Mode) &&
            Exact<Part>(receiver.Part) && ReferenceEquals(_emitting?.Ship, receiver.Part.Ship) && SafeShip(q, receiver.Part.Ship) &&
            (q.Sim.CurrentActionSourcePlayer is not { } source || ExistingPlayer(q.Sim.Game, source.PlayerId, out _));
    }
    private static bool SafePart([NotNullWhen(true)] Part? part) => Exact<Part>(part) && Exact<PartRules>(part.Rules) &&
        Exact<HashSet<ID<PartCategory>>>(part.Rules.TypeCategories) && SafeSet(part.Rules.TypeCategories);

    private static void InspectFixture(Fixture fixture)
    {
        Query? q = _query; if (q == null || q.Value.Invalid) return;
        try
        {
            if (!Exact<Fixture>(fixture) || !Exact<Body>(fixture.Body) ||
                !ReferenceEquals(fixture.Body.World, q.World) || !Exact<Ship>(fixture.Body.UserData)) { InvalidateAll(); return; }
            Type shape = fixture.Shape.GetType();
            if (shape != typeof(PolygonShape) && shape != typeof(CircleShape) && shape != typeof(EdgeShape)) { InvalidateAll(); return; }
            var ship = (Ship)fixture.Body.UserData;
            if (!SafeScopedFilter(q, q.Receiver!.Rules.SecondaryFilter) || !SafeShip(q, ship) || !Exact<ShipPhysics>(ship.Physics)) { InvalidateAll(); return; }
            Type? info = fixture.UserData?.GetType();
            if (info != typeof(ShipPhysics.CellsFixtureInfo) && info != typeof(ShipPhysics.CustomFixtureInfo)) { InvalidateAll(); return; }
            var cells = ship.Parts._partsByCell;
            if (!Exact<Dictionary<Halfling.Geometry.IntVector2, Part>>(cells) ||
                !ReferenceEquals(cells.Comparer, EqualityComparer<Halfling.Geometry.IntVector2>.Default)) InvalidateAll();
        }
        catch { InvalidateAll(); }
    }
    private static bool FilterPart(EffectFilter filter, Part? part, HitEffectFlags flags, bool isDetachedPartValid)
    {
        if (_query != null && !_query.Value.Invalid)
        {
            try
            {
                if (!SafeScopedFilter(_query, filter) || !SafePart(part) ||
                    (!SafeFilter(filter) && !SafeShip(_query, part.Ship))) InvalidateAll();
            }
            catch { InvalidateAll(); }
        }
        return filter.ValidateHitPart(part, flags, isDetachedPartValid);
    }
}
