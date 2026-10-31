using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer.MPInputs;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Blueprints;
using Cosmoteer.Ships.Blueprints.Logic;
using Cosmoteer.Ships.Blueprints.Logic.Values;
using Cosmoteer.Ships.Networks;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Logic;
using Cosmoteer.Ships.Parts.UI;
using Cosmoteer.Source.Ships.Blueprints;
using Halfling.Scene2D;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Update bucket 1 of every ship (<c>PartsManager.UpdateCallbacks</c>) re-evaluates the blueprint-only rules toggle of
/// every blueprint network port, network toggle and stat provider each frame (~23-26% of the main thread with 40 ships).
/// Each callback is level-triggered: it writes its output only when it differs, and the output is a pure function of the
/// part rules, the ship's <c>PartUIToggleStates</c> and (ports) <c>GetToggleModeOverride</c>. A frame in which none of
/// those inputs and no registered callback changed is therefore a native no-op, and only such frames are skipped:
/// <list type="bullet">
/// <item>every toggle-state write site (pinned by an IL scan of all loaded game and mod code) bumps a per-ship version;</item>
/// <item>the callback list is compared by identity and its own List version (register, unregister, part add/remove);</item>
/// <item>native runs whenever an override is active, the ship is selected or edited in the build toolbox, a callback is not
/// one of the three vetted vanilla ones, a foreign patch touches a read dependency, or the previous run threw;</item>
/// <item>after any run outside a validated frame (foreign patch, feature paused) every ship runs native once more;</item>
/// <item>rules data the callbacks read is written only while rules are built; loaded code that could write it is refused.</item>
/// </list>
/// Executed frames are the original method (same snapshot and order). Off (native) when anything cannot be verified; a
/// changed game throws from <see cref="Initialize"/> so every patch rolls back. Reflection writes are not covered.
/// Set PERFORMANCE_BLUEPRINT_TOGGLE_SKIP=0 to keep native updates for A/B comparison.
/// </summary>
internal static class BlueprintToggleUpdates
{
    internal const int Bucket = 1;
    private const long Unseen = long.MinValue;
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_BLUEPRINT_TOGGLE_SKIP"));
    internal static bool IsRequested(string? value) => value != "0";

    internal static readonly MethodInfo Target = AccessTools.Method(typeof(PartsManager.UpdateCallbacks), "Update");

    /// <summary>The only callbacks bucket 1 may contain, each with the exact component type that registers it.</summary>
    internal static readonly (Type Owner, MethodInfo Callback)[] Callbacks =
    [
        (typeof(BlueprintPartNetworkPort), Method(typeof(BaseBlueprintPartNetworkPort), "OnUpdate")),
        (typeof(BlueprintPartStatProvider), Method(typeof(BlueprintPartStatProvider), "OnUpdate")),
        (typeof(BlueprintPartNetworkToggle), Method(typeof(BlueprintPartNetworkToggle), "OnUpdate")),
    ];

    /// <summary>Vanilla implementations of the blueprint toggle/mode interfaces; each reads only rules and the toggle-mode function.</summary>
    internal static readonly Type[] ToggleRules = [typeof(PartUIToggleRules), typeof(PartModeToggleRules), typeof(PartMultiToggleRules), typeof(PartStaticToggleRules), typeof(PartNetworkToggleReceiverRules)];
    internal static readonly Type[] ModeRules = [typeof(PartUIToggleRules), typeof(PartStaticModeRules)];

    /// <summary>Every method that mutates a PartUIToggleStates dictionary. WriteTo fills a local copy only, so it is not hooked.</summary>
    internal static readonly MethodBase[] Writers =
    [
        Method(typeof(MPSetUIToggleMetadataInput), nameof(MPSetUIToggleMetadataInput.Execute)),
        AccessTools.PropertySetter(typeof(PartUIToggle), nameof(PartUIToggle.Mode)),
        Method(typeof(PartMetadata), nameof(PartMetadata.ApplyPreShipUpdate)),
        Method(typeof(ShipMetadata), nameof(ShipMetadata.ReadFrom)),
        Method(typeof(ShipMetadata), nameof(ShipMetadata.WriteTo)),
        Method(typeof(ShipMetadata), nameof(ShipMetadata.CopyPartMetadataFrom)),
        Method(typeof(ShipMetadata), nameof(ShipMetadata.CullPartMetadata)),
        Method(typeof(ShipMetadata), nameof(ShipMetadata.ApplyAllBlueprintStates)),
        typeof(Ship).GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == nameof(Ship.Load) && m.GetParameters()[0].ParameterType.Name == "OBNode"),
    ];

    internal static (MethodBase Target, Type Patch)[] Targets =>
    [
        (Target, typeof(BlueprintToggleUpdates)),
        (Writers[0], typeof(InputToggleWrite)),
        (Writers[1], typeof(ModeToggleWrite)),
        (Writers[2], typeof(PartMetadataToggleWrite)),
        (Writers[3], typeof(ShipMetadataToggleWrite)),
        (Writers[5], typeof(ShipMetadataToggleWrite)),
        (Writers[6], typeof(ShipMetadataToggleWrite)),
        (Writers[7], typeof(ShipMetadataToggleWrite)),
        (Writers[8], typeof(LoadToggleWrite)),
    ];
    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? Targets : [];

    private static MethodInfo Method(Type type, string name) =>
        AccessTools.Method(type, name) ?? throw new MissingMethodException(type.Name, name);

    // ---- Input versions ----------------------------------------------------------------------------------------------

    private sealed class ShipInputs
    {
        public long Version;
    }

    private static readonly ConditionalWeakTable<Ship, ShipInputs> Ships = new();
    private static long _globalVersion;

    /// <summary>Called after every toggle-state write (also when it threw midway); null = ship unknown, all ships re-run.</summary>
    internal static void Bump(Ship? ship)
    {
        if (ship == null) Interlocked.Increment(ref _globalVersion);
        else Interlocked.Increment(ref Ships.GetValue(ship, static _ => new ShipInputs()).Version);
    }

    // ---- Gate --------------------------------------------------------------------------------------------------------

    private sealed class Gate
    {
        public List<SceneComponent.UpdateCallback>? List;
        public int CallbacksVersion;
        public Ship? Ship; // null: list not verified, always native
        public ShipInputs? Inputs;
        public object? Toggles;
        public long Seen = Unseen;
        public long SeenGlobal;
    }

    private static readonly ConditionalWeakTable<PartsManager.UpdateCallbacks, Gate> Gates = new();
    [ThreadStatic] private static bool _frameValidated;
    private static int _ready;
    private static string? _failure;
    private static bool _loggedActive;
    private static bool _loggedForeign;

    internal static bool Ready => Volatile.Read(ref _ready) != 0;
    internal static bool IsFrameValidated => _frameValidated;
    internal static string? FailureReason => Volatile.Read(ref _failure);

    /// <summary>Set by any bucket-1 run outside a validated frame: its outputs are untracked, so every ship re-runs once.</summary>
    private static int _unvalidatedRun;

    internal static void BeginFrame(bool validated)
    {
        _frameValidated = validated && Ready;
        if (_frameValidated && Volatile.Read(ref _unvalidatedRun) != 0 && Interlocked.Exchange(ref _unvalidatedRun, 0) != 0)
            Interlocked.Increment(ref _globalVersion);
    }

    internal static void EndFrame() => _frameValidated = false;

    /// <summary>Native must run unless inputs are unchanged and no override, selection or build toolbox may observe the frame.</summary>
    internal static bool MustRunNative(bool inputsUnchanged, bool overrideActive, bool selected, bool buildTarget) =>
        !inputsUnchanged || overrideActive || selected || buildTarget;

    public static bool Prefix(PartsManager.UpdateCallbacks __instance, out bool __state)
    {
        __state = false;
        if (__instance.UpdateBucket != Bucket) return true;
        if (!_frameValidated)
        {
            // Foreign patch, feature off or outside the interval: native may write outputs no version tracks.
            if (Volatile.Read(ref _unvalidatedRun) == 0) Volatile.Write(ref _unvalidatedRun, 1);
            return true;
        }
        try
        {
            Gate gate = Gates.GetValue(__instance, static _ => new Gate());
            List<SceneComponent.UpdateCallback> list = __instance.Callbacks;
            int listVersion = CallbackVersion(list);
            if (!ReferenceEquals(gate.List, list) || gate.CallbacksVersion != listVersion)
            {
                gate.List = list;
                gate.CallbacksVersion = listVersion;
                gate.Ship = VerifiedShip(list);
                gate.Inputs = gate.Ship == null ? null : Ships.GetValue(gate.Ship, static _ => new ShipInputs());
                gate.Seen = Unseen;
            }
            Ship? ship = gate.Ship;
            if (ship == null) return true;
            long version = Volatile.Read(ref gate.Inputs!.Version);
            long global = Volatile.Read(ref _globalVersion);
            object toggles = ship.Metadata.PartUIToggleStates;
            bool overridden = ship.BlueprintParts.GetToggleModeOverride != null;
            bool unchanged = gate.Seen == version && gate.SeenGlobal == global && ReferenceEquals(gate.Toggles, toggles);
            if (!MustRunNative(unchanged, overridden, IsSelected(ship), IsBuildTarget(ship)))
            {
                if (!_loggedActive)
                {
                    _loggedActive = true;
                    Log.Info("Blueprint toggle update skip active: unchanged bucket-1 frames skipped");
                }
                return false;
            }
            // Captured before the native run: writes made by the callbacks themselves force another run next frame.
            gate.Seen = overridden ? Unseen : version;
            gate.SeenGlobal = global;
            gate.Toggles = toggles;
            __state = true;
            return true;
        }
        catch (Exception ex)
        {
            Reject(ex.Message);
            return true;
        }
    }

    /// <summary>A native run that threw is repeated next frame, as vanilla would.</summary>
    public static void Finalizer(PartsManager.UpdateCallbacks __instance, bool __state, Exception? __exception)
    {
        if (__state && __exception != null && Gates.TryGetValue(__instance, out Gate? gate)) gate.Seen = Unseen;
    }

    private static bool IsSelected(Ship ship) => ship.Sim?.PlayerInput.SelectedShips.Contains(ship) == true;

    private static bool IsBuildTarget(Ship ship) =>
        ship.Game?.Gui.ShipGui.BuildToolbox is { } toolbox && ReferenceEquals(toolbox._ship, ship) && toolbox.SelfActive;

    /// <summary>The ship of every callback, or null unless each is a single-target vetted callback on one ship.</summary>
    internal static Ship? VerifiedShip(List<SceneComponent.UpdateCallback> list)
    {
        Ship? ship = null;
        foreach (SceneComponent.UpdateCallback callback in list)
        {
            if (!callback.HasSingleTarget || !IsVetted(callback.Target, callback.Method)) return null;
            Ship? owner = ((BlueprintPartComponent)callback.Target!).Ship;
            if (owner == null || (ship != null && !ReferenceEquals(ship, owner))) return null;
            ship = owner;
        }
        return ship;
    }

    internal static bool IsVetted(object? target, MethodInfo method)
    {
        for (int i = 0; i < Callbacks.Length; i++)
        {
            if (target?.GetType() != Callbacks[i].Owner || !BlueprintToggleEvaluation.SameMethodDefinition(method, Callbacks[i].Callback)) continue;
            return target switch
            {
                BlueprintPartNetworkPort port => IsVettedToggle(port._rulesOperationalToggle),
                BlueprintPartStatProvider stat => stat._operationalToggle == null || IsVettedToggle(stat._operationalToggle),
                BlueprintPartNetworkToggle toggle => IsVettedToggle(toggle._rulesToggleSource),
                _ => false,
            };
        }
        return false;
    }

    private static bool IsVettedToggle(IBlueprintComponentToggle? toggle) => toggle != null && Array.IndexOf(ToggleRules, toggle.GetType()) >= 0;

    /// <summary>List&lt;T&gt; increments _version on every mutation (Add, Remove, Insert, indexer set, Clear, Sort...).</summary>
    internal static class ListAccess<T>
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_version")]
        internal static extern ref int Version(List<T> list);
    }

    private static int CallbackVersion(List<SceneComponent.UpdateCallback> list) => ListAccess<SceneComponent.UpdateCallback>.Version(list);

    /// <summary>The callback list's version must change with each kind of mutation the game performs.</summary>
    internal static void ValidateListVersion()
    {
        var list = new List<SceneComponent.UpdateCallback>();
        SceneComponent.UpdateCallback callback = static _ => { };
        int last = CallbackVersion(list);
        Action[] mutations = [() => list.Add(callback), () => list[0] = callback, () => list.Remove(callback), () => list.Add(callback), () => list.Clear()];
        foreach (Action mutate in mutations)
        {
            mutate();
            int version = CallbackVersion(list);
            if (version == last) throw new NotSupportedException("List<T>._version does not track mutations");
            last = version;
        }
    }

    // ---- Startup verification ----------------------------------------------------------------------------------------

    private static readonly object InitLock = new();
    private static readonly ConditionalWeakTable<Assembly, StrongBox<string?>> Scanned = new();
    private static string? _nativeFailure; // null = not yet validated, "" = validated
    private static string? _loadFailure; // sticky: an assembly loaded after startup failed the scan
    private static HashSet<(Module, int)>? _dependencySet;
    private static bool _watchingLoads;

    /// <summary>Assembly check used at startup and on every later load; tests exclude their own fixtures.</summary>
    internal static Func<Assembly, string?> Scan = assembly => Scanned.GetValue(assembly, a => new StrongBox<string?>(ScanAssembly(a))).Value;

    /// <summary>Read dependencies checked for foreign patches every frame: the target, the callback closure and the leaf's.</summary>
    internal static bool IsDependency(MethodBase method) =>
        _dependencySet != null && _dependencySet.Contains((method.Module, method.MetadataToken));

    /// <summary>
    /// Throws when the game or this mod's hooks no longer match (changed IL, write or rules-access sites, missing hook), so
    /// <see cref="Patcher"/> rolls back every patch. Foreign code that could change inputs unseen only keeps this feature
    /// native (returns false); the rest of the mod stays active.
    /// </summary>
    internal static bool Initialize()
    {
        if (!Requested) return true;
        lock (InitLock)
        {
            Volatile.Write(ref _failure, null);
            try
            {
                RequireNative();
                foreach ((MethodBase target, Type patch) in Targets)
                {
                    Patches? patches = Harmony.GetPatchInfo(target);
                    if (patches == null || !patches.Finalizers.Any(p => p.owner == Patcher.HarmonyId && p.PatchMethod.DeclaringType == patch))
                        throw new InvalidOperationException("Blueprint toggle update hook missing on " + target.DeclaringType?.Name + "." + target.Name);
                    if (!Patcher.TryDisableInlining(target, out string? error)) throw new NotSupportedException(error);
                }
            }
            catch (Exception ex)
            {
                Reject(ex.Message);
                throw;
            }
            try
            {
                if (!_watchingLoads)
                {
                    _watchingLoads = true;
                    AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad; // before the scan, so no assembly falls between
                }
                if (ScanForeign() is { } foreign) throw new NotSupportedException(foreign);
                Interlocked.Increment(ref _globalVersion); // nothing seen before this point is trusted
                Volatile.Write(ref _ready, 1);
                if (Volatile.Read(ref _loadFailure) is { } late) throw new NotSupportedException(late); // loaded during the scan
                Log.Info("Blueprint toggle update skip ready");
                return true;
            }
            catch (Exception ex)
            {
                Reject(ex.Message);
                return false;
            }
        }
    }

    /// <summary>Native premises: List versioning, the pinned read/write IL and the game's own write and rules-access sites.</summary>
    private static void RequireNative()
    {
        if (_nativeFailure == null)
        {
            try
            {
                ValidateListVersion();
                ValidateNative();
                _nativeFailure = "";
            }
            catch (Exception ex)
            {
                _nativeFailure = ex.Message;
            }
        }
        if (_nativeFailure.Length != 0) throw new NotSupportedException(_nativeFailure);
        if (Scan(typeof(Ship).Assembly) is { } game) throw new NotSupportedException(game);
    }

    private static string? ScanForeign()
    {
        if (Volatile.Read(ref _loadFailure) is { } late) return late;
        Assembly game = typeof(Ship).Assembly;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (assembly != game && Scan(assembly) is { } reason) return reason;
        return null;
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        string? reason;
        try
        {
            reason = Scan(args.LoadedAssembly);
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }
        if (reason == null) return;
        Interlocked.CompareExchange(ref _loadFailure, reason, null);
        Reject(reason);
    }

    internal static void Reject(string reason)
    {
        Volatile.Write(ref _ready, 0);
        _frameValidated = false;
        if (Interlocked.CompareExchange(ref _failure, reason, null) == null) Log.Info("Blueprint toggle update skip disabled: " + reason);
    }

    internal static void LogForeign(string reason)
    {
        if (_loggedForeign) return;
        _loggedForeign = true;
        Log.Info("Blueprint toggle update skip paused (native updates): " + reason);
    }

    internal static void Disable()
    {
        Volatile.Write(ref _ready, 0);
        _frameValidated = false;
    }

    /// <summary>Native read path: Update, the callbacks, their toggle evaluation and the hooked write sites, pinned by one IL hash.</summary>
    internal static List<MethodBase> ReadClosure()
    {
        var roots = new List<MethodBase> { Target };
        roots.AddRange(Callbacks.Select(c => c.Callback));
        foreach (Type type in ToggleRules) roots.AddRange(type.GetInterfaceMap(typeof(IBlueprintComponentToggle)).TargetMethods);
        foreach (Type type in ModeRules) roots.AddRange(type.GetInterfaceMap(typeof(IBlueprintComponentMode)).TargetMethods);
        Type networks = typeof(ShipPartNetworkManager<,,>);
        // Not followed: network (de)registration (runs only when an output changes, never on a skipped frame); interface
        // dispatch (every vanilla implementation is a root, others are refused by the scan); delegate Invoke (no IL; the
        // delegates invoked are the vetted callbacks and their own GetToggleMode).
        return BlueprintToggleEvaluation.ReadClosure(roots, followFunctionPointers: true, method =>
            (method.DeclaringType is { IsGenericType: true } type && type.GetGenericTypeDefinition() == networks) ||
            method.DeclaringType == typeof(IBlueprintComponentToggle) || method.DeclaringType == typeof(IBlueprintComponentMode) ||
            (method.Name == "Invoke" && typeof(Delegate).IsAssignableFrom(method.DeclaringType)));
    }

    internal static void ValidateNative()
    {
        List<MethodBase> closure = ReadClosure();
        Patcher.RequireIl(closure.Concat(Writers).SelectMany(m => PatchProcessor.GetOriginalInstructions(m)),
            "26C8E3A1ABD76B68ED16D1AD7B97E73F5EE18BDA6FE8FAB42B2A5EB9AC090641", "Blueprint toggle update dependencies");
        BlueprintToggleEvaluation.ValidateNativeDependencies();
        var set = new HashSet<(Module, int)>();
        foreach (MethodBase method in closure.Concat(BlueprintToggleEvaluation.Dependencies)) set.Add((method.Module, method.MetadataToken));
        _dependencySet = set;
    }

    // ---- IL scan of loaded code ----------------------------------------------------------------------------------------

    private static readonly Type ToggleStates = typeof(PartStateDictionary<(PartSaveData, ID<PartToggleGuiRules>), int>);
    private static readonly OpCode[] OneByte = new OpCode[256];
    private static readonly OpCode[] TwoByte = new OpCode[256];

    static BlueprintToggleUpdates()
    {
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var op = (OpCode)field.GetValue(null)!;
            ushort value = unchecked((ushort)op.Value);
            if (op.Size == 1) OneByte[value] = op;
            else TwoByte[value & 0xFF] = op;
        }
    }

    /// <summary>
    /// Rules data the read closure loads (fields of part/component toggle rules, their nested rules and the components'
    /// toggle sources). The game writes them only while building rules: in their constructors or by reflection while
    /// loading data, before any ship exists; there is no in-place rules reload. Keyed by (module, token).
    /// </summary>
    private static HashSet<(Module, int)>? _inputFields;

    private static HashSet<(Module, int)> InputFields => _inputFields ??= ReadInputFields();

    private static HashSet<(Module, int)> ReadInputFields()
    {
        var set = new HashSet<(Module, int)>();
        foreach (MethodBase method in ReadClosure().Concat(BlueprintToggleEvaluation.Dependencies))
            foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(method))
                if (instruction.operand is FieldInfo field && IsInputField(field)) set.Add((field.Module, field.MetadataToken));
        return set;
    }

    internal static bool IsInputField(FieldInfo field) =>
        typeof(IBlueprintComponentToggle).IsAssignableFrom(field.FieldType) || typeof(IBlueprintComponentMode).IsAssignableFrom(field.FieldType) ||
        IsRulesType(field.DeclaringType);

    private static bool IsRulesType(Type? type) =>
        type != null && !type.IsDefined(typeof(CompilerGeneratedAttribute), false) &&
        (typeof(PartComponentRules).IsAssignableFrom(type) || type == typeof(PartRules) || type == typeof(ComponentToggleReferenceRules) ||
            IsRulesType(type.DeclaringType));

    /// <summary>Collections can be mutated through a plain load (List.Add, array store), so loading one counts as access.</summary>
    private static bool IsContainer(Type type) => type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);

    /// <summary>
    /// SHA-256 of the game's own rules-data access sites that could write (stores, addresses, tokens, collection loads):
    /// constructors and data loading, plus read-only UI. A game update that changes them disables (rolls back) the mod.
    /// </summary>
    internal const string GameInputSites = "805934C349E38651A646603D55EE3B6A0B5C18D02A1C6DE160A2610662291F78";

    /// <summary>Dynamic assemblies of the loader's Harmony 2.4 (MonoMod) and the runtime's DynamicMethod host.</summary>
    private static readonly string[] KnownDynamic =
        ["HarmonySharedState", "HarmonyDTFAssembly", "MonoMod.Utils.Cil.ILGeneratorProxy", "Anonymously Hosted DynamicMethods Assembly"];

    internal static bool IsKnownDynamic(Assembly assembly) =>
        assembly.GetName().Name is { } name && (Array.IndexOf(KnownDynamic, name) >= 0 || name.StartsWith("DMDASM.", StringComparison.Ordinal));

    /// <summary>
    /// Null when <paramref name="assembly"/> cannot change toggle states outside <see cref="Writers"/>, cannot change rules
    /// data the callbacks read and adds no blueprint toggle/mode implementation; otherwise the reason. Only assemblies that
    /// can name Cosmoteer types are read. Anything unreadable is refused. Not covered: reflection and runtime-emitted
    /// DynamicMethods (Harmony replacements are built from scanned methods).
    /// </summary>
    internal static string? ScanAssembly(Assembly assembly)
    {
        Assembly game = typeof(Ship).Assembly;
        string name = assembly.GetName().Name ?? "?";
        if (assembly.IsDynamic) return IsKnownDynamic(assembly) ? null : "unknown dynamic assembly " + name;
        try
        {
            if (assembly != game && !assembly.GetReferencedAssemblies().Any(n => n.Name == game.GetName().Name)) return null;
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return "cannot read types of " + name + ": " + ex.LoaderExceptions.FirstOrDefault()?.Message;
            }
            return ScanTypes(assembly, types);
        }
        catch (Exception ex)
        {
            return "cannot scan " + name + ": " + ex.Message;
        }
    }

    internal static string? ScanTypes(Assembly assembly, IEnumerable<Type> types)
    {
        Assembly game = typeof(Ship).Assembly;
        bool own = assembly == typeof(BlueprintToggleUpdates).Assembly;
        var state = new ScanState();
        var gameSites = new List<string>();
        int writeSites = 0;
        foreach (Type type in types)
        {
            bool toggle = typeof(IBlueprintComponentToggle).IsAssignableFrom(type) && Array.IndexOf(ToggleRules, type) < 0;
            bool mode = typeof(IBlueprintComponentMode).IsAssignableFrom(type) && Array.IndexOf(ModeRules, type) < 0;
            if (!type.IsInterface && (toggle || mode)) return "unvetted blueprint toggle implementation " + type.FullName;
            bool stateOwnCode = IsToggleStatesOwnCode(type);
            const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            foreach (MethodBase method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                state.Inputs.Clear();
                if (ScanMethod(method, state) && !stateOwnCode)
                {
                    if (!Writers.Any(w => BlueprintToggleEvaluation.SameMethodDefinition(w, method))) return "unvetted toggle-state write in " + Name(method);
                    writeSites++;
                }
                foreach ((OpCode op, FieldInfo field) in state.Inputs)
                {
                    bool load = op == OpCodes.Ldfld || op == OpCodes.Ldsfld;
                    if (load && !IsContainer(field.FieldType)) continue; // plain value read
                    if (assembly == game) gameSites.Add(op.Name + " " + field.DeclaringType?.FullName + "." + field.Name + " @ " + Name(method));
                    else if (!(load && own)) return "rules data access (" + op.Name + " " + field.DeclaringType?.Name + "." + field.Name + ") in " + Name(method);
                }
            }
        }
        if (assembly == game)
        {
            if (writeSites != Writers.Length) return "toggle-state write sites changed (" + writeSites + " found)";
            gameSites.Sort(StringComparer.Ordinal);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", gameSites))));
            if (hash != GameInputSites) return "rules data access sites changed (sha256 " + hash + ", " + gameSites.Count + " sites)";
        }
        return null;
    }

    private static string Name(MethodBase method) => method.DeclaringType?.FullName + "." + method.Name;

    internal sealed class ScanState
    {
        public readonly Dictionary<int, object?> Cache = new();
        public readonly List<(OpCode Op, FieldInfo Field)> Inputs = [];
    }

    private static bool IsToggleStatesOwnCode(Type? type)
    {
        for (; type != null; type = type.DeclaringType)
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PartStateDictionary<,>)) return true;
        return false;
    }

    /// <summary>True when the IL calls, references or reads the storage of a toggle-state mutator (open instantiations included).</summary>
    internal static bool WritesToggleStates(MethodBase method) => ScanMethod(method, new ScanState());

    /// <summary>
    /// Reads one method's IL: returns whether it references a toggle-state mutator and appends every rules-data field access
    /// to <see cref="ScanState.Inputs"/>. Throws when a method that must have a body has none or it cannot be read.
    /// </summary>
    private static bool ScanMethod(MethodBase method, ScanState state)
    {
        MethodImplAttributes impl = method.MethodImplementationFlags;
        if (method.IsAbstract || method.Attributes.HasFlag(MethodAttributes.PinvokeImpl) ||
            impl.HasFlag(MethodImplAttributes.InternalCall) || impl.HasFlag(MethodImplAttributes.Native) || impl.HasFlag(MethodImplAttributes.OPTIL)) return false; // Native|OPTIL = Runtime
        // The runtime generates these bodies; one whose signature names a game type could reach any of its fields.
        if (method.IsDefined(typeof(UnsafeAccessorAttribute), false))
            return NamesGameType(method) ? throw new NotSupportedException("unsafe accessor into game types: " + Name(method)) : false;
        byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? throw new NotSupportedException("unreadable IL of " + Name(method));
        Module module = method.Module;
        Type[]? typeArgs = method.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
        Type[]? methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        bool writes = false;
        for (int i = 0; i < il.Length;)
        {
            OpCode op = il[i] == 0xFE ? TwoByte[il[i + 1]] : OneByte[il[i]];
            if (op.Size == 0) throw new NotSupportedException("invalid IL of " + Name(method));
            i += op.Size;
            switch (op.OperandType)
            {
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineTok:
                    object? kind = Classify(module, BitConverter.ToInt32(il, i), typeArgs, methodArgs, state.Cache);
                    if (ReferenceEquals(kind, MutatorMark)) writes = true;
                    else if (kind is FieldInfo field) state.Inputs.Add((op, field));
                    i += 4;
                    break;
                case OperandType.InlineSwitch:
                    i += 4 + 4 * BitConverter.ToInt32(il, i);
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    i += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    i += 8;
                    break;
                default:
                    i += 4;
                    break;
            }
        }
        return writes;
    }

    private static bool NamesGameType(MethodBase method)
    {
        Assembly game = typeof(Ship).Assembly;
        bool Names(Type type) => type.HasElementType ? Names(type.GetElementType()!) : type.Assembly == game || type.GetGenericArguments().Any(Names);
        return method.GetParameters().Any(p => Names(p.ParameterType)) || (method is MethodInfo info && Names(info.ReturnType));
    }

    private static readonly object MutatorMark = new();

    /// <summary><see cref="MutatorMark"/> for a toggle-state mutator, the field for a rules-data input, otherwise null.</summary>
    private static object? Classify(Module module, int token, Type[]? typeArgs, Type[]? methodArgs, Dictionary<int, object?> cache)
    {
        int table = token >>> 24;
        if (table != 0x06 && table != 0x0A && table != 0x2B && table != 0x04) return null; // methods, member refs, method specs, fields
        if (cache.TryGetValue(token, out object? known)) return known;
        MemberInfo? member;
        bool cacheable = true;
        try
        {
            member = module.ResolveMember(token);
        }
        catch (ArgumentException)
        {
            // Signature uses the caller's generic parameters: resolve in its context (not cacheable).
            member = module.ResolveMember(token, typeArgs, methodArgs);
            cacheable = false;
        }
        object? result = IsMutator(member) ? MutatorMark
            : member is FieldInfo field && InputFields.Contains((field.Module, field.MetadataToken)) ? field : null;
        if (cacheable) cache[token] = result;
        return result;
    }
    internal static bool IsMutator(MemberInfo? member)
    {
        if (member?.DeclaringType is not { IsGenericType: true } type || type.GetGenericTypeDefinition() != typeof(PartStateDictionary<,>)) return false;
        if (type != ToggleStates && !type.ContainsGenericParameters) return false; // other instantiation (colors)
        return member switch
        {
            MethodBase m => m.Name is nameof(PartStateDictionary<int, int>.Set) or nameof(PartStateDictionary<int, int>.Clear)
                or nameof(PartStateDictionary<int, int>.RemoveAll) or nameof(PartStateDictionary<int, int>.ApplyAllBlueprintStates),
            FieldInfo f => f.Name is "_states" or "_blueprintStates",
            _ => false,
        };
    }
}

/// <summary>Toggle-state write hooks: finalizers run after the write, also when it threw midway.</summary>
internal static class InputToggleWrite
{
    public static void Finalizer(MPSetUIToggleMetadataInput __instance) => BlueprintToggleUpdates.Bump(__instance.Ship);
}

internal static class ModeToggleWrite
{
    public static void Prefix(PartUIToggle __instance, int value, out bool __state) => __state = value != __instance.Mode;

    public static void Finalizer(PartUIToggle __instance, bool __state)
    {
        if (__state) BlueprintToggleUpdates.Bump(__instance.Ship);
    }
}

internal static class PartMetadataToggleWrite
{
    public static void Finalizer(Ship ship) => BlueprintToggleUpdates.Bump(ship);
}

internal static class ShipMetadataToggleWrite
{
    public static void Finalizer(ShipMetadata __instance) => BlueprintToggleUpdates.Bump(__instance.Ship);
}

/// <summary>Ship.Load writes only the states of the ship it creates; without a result (it threw) every ship re-runs.</summary>
internal static class LoadToggleWrite
{
    public static void Finalizer(Ship? __result) => BlueprintToggleUpdates.Bump(__result);
}
