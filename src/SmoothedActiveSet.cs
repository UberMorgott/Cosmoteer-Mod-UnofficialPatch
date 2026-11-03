using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Cosmoteer.Ships.Parts.Logic;
using Halfling.Scene2D;
using Halfling.Timing;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>
/// <c>PartSmoothedValue.SmoothedValueManager.Update</c> (update bucket 7) calls <c>UpdateValue</c> on every
/// non-deterministic smoothed value of the ship each frame (vanilla: radiator and heat exchanger effects, ~2160 values with
/// 40 cruisers, ~92% already equal to their source). <c>UpdateValue</c> is a native no-op while
/// <c>Value.Equals(source.Value)</c>; a value only leaves that state when its source's or its own <c>Value</c> changes.
/// <list type="bullet">
/// <item>Sources whose exact type is audited (<see cref="Sources"/>) keep <c>Value</c> in a field written only by a setter
/// that raises <c>ValueChanged</c>; their values sleep after an exact native-equality check and wake on either event. A
/// setter that throws (another subscriber failed, possibly before the wake ran) wakes every value (<see cref="SetterHook"/>).</item>
/// <item>Removal releases the removed value's subscriptions at once (<see cref="RemovalHook"/>); disabling releases all.</item>
/// <item>Any other or replaced source, and every value after a list change or a paused frame, runs native every frame.</item>
/// <item>Awake values run the native <c>UpdateValue</c> with the native delta in the native list order; a list change made
/// by a call throws like the native enumerator.</item>
/// </list>
/// Values and outputs (including the radiator's thermal_load_dissipation network value) are those of vanilla: only calls
/// that would be no-ops are skipped. Off (native) while a foreign Harmony patch touches a dependency; a changed game
/// throws from <see cref="Initialize"/> so every patch rolls back. Raw reflection writes to the value fields are not covered.
/// Set UNOFFICIAL_PATCH_SMOOTHED_ACTIVE_SET=0 to keep native updates for A/B comparison.
/// </summary>
internal static class SmoothedActiveSet
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("UNOFFICIAL_PATCH_SMOOTHED_ACTIVE_SET"));
    internal static bool IsRequested(string? value) => value != "0";

    internal static readonly MethodInfo Target = Method(typeof(PartSmoothedValue.SmoothedValueManager), "Update");

    /// <summary>The only remover of non-deterministic values; the last removal also stops the manager's Update.</summary>
    internal static readonly MethodInfo RemoveTarget = Method(typeof(PartSmoothedValue.SmoothedValueManager), "RemoveNonDeterministic");

    /// <summary>Source types whose <c>Value</c> changes only through a setter that raises <c>ValueChanged</c>.</summary>
    internal static readonly Type[] Sources = [typeof(PartToggleValueSelector), typeof(PartValueOperation)];

    /// <summary>Types whose Value / ValueChanged storage is audited: the smoothed value itself and every source.</summary>
    private static readonly Type[] ValueTypes = [typeof(PartSmoothedValue), .. Sources];

    /// <summary>The audited setters (the only writers of each Value, see <see cref="RequireWriters"/>).</summary>
    internal static MethodInfo[] Setters => ValueTypes.Select(t => AccessTools.PropertySetter(t, "Value") ?? throw new MissingMethodException(t.Name, "set_Value")).ToArray();

    internal static (MethodBase Target, Type Patch)[] Targets =>
        [(Target, typeof(SmoothedActiveSet)), (RemoveTarget, typeof(RemovalHook)), .. Setters.Select(s => ((MethodBase)s, typeof(SetterHook)))];

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? Targets : [];

    private static MethodInfo Method(Type type, string name) =>
        AccessTools.Method(type, name) ?? throw new MissingMethodException(type.Name, name);

    /// <summary>The bypassed Update, the hooked remover, the called UpdateValue, and Value / ValueChanged of every audited type.</summary>
    internal static List<MethodBase> Dependencies()
    {
        var list = new List<MethodBase> { Target, RemoveTarget, Method(typeof(PartSmoothedValue), "UpdateValue") };
        foreach (Type type in ValueTypes)
        {
            list.Add(AccessTools.PropertyGetter(type, "Value"));
            list.Add(AccessTools.PropertySetter(type, "Value"));
            list.Add(Method(type, "add_ValueChanged"));
            list.Add(Method(type, "remove_ValueChanged"));
        }
        return list;
    }

    internal const string DependenciesIl = "6478B9702B95AFC7690829CF1C736E6E17220336E5915B3029F6F080A6F7F22D";

    // ---- Per-manager state -------------------------------------------------------------------------------------------

    internal sealed class Entry
    {
        public readonly PartSmoothedValue Value;
        public readonly EventHandler<EventArgs> Wake;
        public IComponentValueProvider? Source;
        public bool Tracked; // Source is audited and subscribed
        public bool Awake = true;

        public Entry(PartSmoothedValue value)
        {
            Value = value;
            Wake = OnChanged;
            value.ValueChanged += Wake;
        }

        private void OnChanged(object? sender, EventArgs e) => Awake = true;

        public void Bind(IComponentValueProvider? source)
        {
            if (Tracked) Source!.ValueChanged -= Wake;
            Source = source;
            Tracked = source != null && Array.IndexOf(Sources, source.GetType()) >= 0;
            if (Tracked) source!.ValueChanged += Wake;
            Awake = true;
        }

        public void Unbind()
        {
            if (Tracked) Source!.ValueChanged -= Wake;
            Value.ValueChanged -= Wake;
            Tracked = false;
            Source = null;
        }
    }

    internal sealed class State
    {
        public List<PartSmoothedValue>? List;
        public int Version;
        public long Epoch = -1;
        public List<Entry> Entries = [];
        public List<Entry> Spare = [];
        public readonly Dictionary<PartSmoothedValue, Entry> Scratch = new(ReferenceEqualityComparer.Instance);
    }

    private static readonly ConditionalWeakTable<PartSmoothedValue.SmoothedValueManager, State> States = new();

    /// <summary>Bumped when a frame ran without validation: every value is woken before it may sleep again.</summary>
    private static long _epoch;
    private static int _unvalidatedRun;
    [ThreadStatic] private static bool _frameValidated;
    private static int _ready;
    private static string? _failure;
    private static bool _loggedActive;
    private static bool _loggedForeign;
    private static HashSet<(Module, int)>? _dependencySet;

    internal static bool Ready => Volatile.Read(ref _ready) != 0;
    internal static string? FailureReason => Volatile.Read(ref _failure);

    internal static bool IsDependency(MethodBase method) => _dependencySet != null && _dependencySet.Contains((method.Module, method.MetadataToken));

    internal static void BeginFrame(bool validated)
    {
        _frameValidated = validated && Ready;
        if (_frameValidated && Volatile.Read(ref _unvalidatedRun) != 0 && Interlocked.Exchange(ref _unvalidatedRun, 0) != 0)
            Interlocked.Increment(ref _epoch);
    }

    internal static void EndFrame() => _frameValidated = false;

    /// <summary>Reconciles the entries with the native list (same order and duplicates); kept entries are woken and rebound.</summary>
    internal static void Sync(State state, List<PartSmoothedValue> list, int version, long epoch)
    {
        Dictionary<PartSmoothedValue, Entry> scratch = state.Scratch;
        List<Entry> next = state.Spare;
        next.Clear();
        foreach (Entry entry in state.Entries)
            if (!scratch.TryAdd(entry.Value, entry)) entry.Unbind();
        foreach (PartSmoothedValue value in list)
        {
            if (!scratch.Remove(value, out Entry? entry)) entry = new Entry(value);
            entry.Bind(value._valueSource);
            next.Add(entry);
        }
        foreach (Entry stale in scratch.Values) stale.Unbind();
        scratch.Clear();
        state.Spare = state.Entries;
        state.Spare.Clear();
        state.Entries = next;
        state.List = list;
        state.Version = version;
        state.Epoch = epoch;
    }

    /// <summary>Unsubscribes every entry; the next Step rebuilds the state from the native list.</summary>
    internal static void Release(State state)
    {
        foreach (Entry entry in state.Entries) entry.Unbind();
        state.Entries.Clear();
        state.List = null;
    }

    /// <summary>Releases the subscriptions of every manager (feature off for good, or rolled back).</summary>
    private static void ReleaseAll()
    {
        foreach (KeyValuePair<PartSmoothedValue.SmoothedValueManager, State> pair in States) Release(pair.Value);
        States.Clear();
    }

    /// <summary>
    /// Removal is reconciled at once: the last removal stops the manager's Update, so no later Step would release the removed
    /// value's subscriptions. Runs whether or not the feature is active; a value removed mid-Step makes Step throw like native.
    /// </summary>
    public static class RemovalHook
    {
        public static void Finalizer(PartSmoothedValue.SmoothedValueManager __instance)
        {
            if (!States.TryGetValue(__instance, out State? state) || state.List == null) return;
            try
            {
                List<PartSmoothedValue> list = __instance._nonDeterministicValues;
                Sync(state, list, BlueprintToggleUpdates.ListAccess<PartSmoothedValue>.Version(list), state.Epoch);
            }
            catch (Exception ex)
            {
                Reject(ex.Message);
            }
        }
    }

    /// <summary>
    /// A setter raises ValueChanged after storing the new value; a subscriber that throws before an entry's Wake would leave
    /// the entry asleep on a changed value. The exception is kept (void finalizer) and every value is woken.
    /// </summary>
    public static class SetterHook
    {
        public static void Finalizer(Exception? __exception)
        {
            if (__exception != null) Interlocked.Increment(ref _epoch);
        }
    }

    /// <summary>Native Update for awake values only; same delta, order and enumerator semantics.</summary>
    public static bool Prefix(PartSmoothedValue.SmoothedValueManager __instance, SceneRoot root)
    {
        if (!_frameValidated)
        {
            if (Volatile.Read(ref _unvalidatedRun) == 0) Volatile.Write(ref _unvalidatedRun, 1);
            return true;
        }
        return Step(__instance, float.IsNaN(root.Clock.DeltaTime) ? ((Time)0f) : root.Clock.DeltaTime);
    }

    /// <summary>False after running the awake values; true (run native) when the bookkeeping failed.</summary>
    internal static bool Step(PartSmoothedValue.SmoothedValueManager __instance, float num)
    {
        State state;
        List<PartSmoothedValue> list;
        int version;
        try
        {
            state = States.GetValue(__instance, static _ => new State());
            list = __instance._nonDeterministicValues;
            version = BlueprintToggleUpdates.ListAccess<PartSmoothedValue>.Version(list);
            long epoch = Volatile.Read(ref _epoch);
            if (!ReferenceEquals(state.List, list) || state.Version != version || state.Epoch != epoch) Sync(state, list, version, epoch);
        }
        catch (Exception ex)
        {
            Reject(ex.Message);
            return true;
        }
        if (!_loggedActive)
        {
            _loggedActive = true;
            Log.Info("Smoothed value active set active: settled non-deterministic values sleep until their source changes");
        }
        List<Entry> entries = state.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            PartSmoothedValue value = entry.Value;
            if (!ReferenceEquals(value._valueSource, entry.Source)) entry.Bind(value._valueSource);
            // A setter that threw during this Step (SetterHook) bumped the epoch: every later value runs, as after a Sync.
            else if (!entry.Awake && Volatile.Read(ref _epoch) == state.Epoch) continue;
            value.UpdateValue(num);
            if (BlueprintToggleUpdates.ListAccess<PartSmoothedValue>.Version(list) != version)
                throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
            // Events raised during the call only set Awake; the exact native no-op test decides.
            if (entry.Tracked) entry.Awake = !value.Value.Equals(entry.Source!.Value);
        }
        return false;
    }

    // ---- Startup verification ----------------------------------------------------------------------------------------

    /// <summary>Throws when the game changed (rolls back every patch); false keeps only this feature native.</summary>
    internal static bool Initialize()
    {
        if (!Requested) return true;
        try
        {
            ValidateNative();
            foreach ((MethodBase target, Type patch) in Targets)
            {
                Patches? patches = Harmony.GetPatchInfo(target);
                if (patches == null || !patches.Prefixes.Concat(patches.Finalizers).Any(p => p.owner == Patcher.HarmonyId && p.PatchMethod.DeclaringType == patch))
                    throw new InvalidOperationException("Smoothed value active set hook missing on " + target.DeclaringType?.Name + "." + target.Name);
                if (!Patcher.TryDisableInlining(target, out string? error)) throw new NotSupportedException(error);
            }
        }
        catch (Exception ex)
        {
            Reject(ex.Message);
            throw;
        }
        Interlocked.Increment(ref _epoch);
        Volatile.Write(ref _ready, 1);
        Log.Info("Smoothed value active set ready");
        return true;
    }

    /// <summary>Pinned IL of every dependency, the List version premise and the only writers of each audited field.</summary>
    internal static void ValidateNative()
    {
        BlueprintToggleUpdates.ValidateListVersion();
        List<MethodBase> dependencies = Dependencies();
        Patcher.RequireIl(dependencies.SelectMany(m => PatchProcessor.GetOriginalInstructions(m)), DependenciesIl, "Smoothed value active set dependencies");
        foreach (Type type in ValueTypes) RequireWriters(type);
        _dependencySet = dependencies.Select(m => (m.Module, m.MetadataToken)).ToHashSet();
    }

    /// <summary>Value's backing field is written only by set_Value, the event field only by add/remove (nested types included).</summary>
    internal static void RequireWriters(Type type)
    {
        FieldInfo value = AccessTools.Field(type, "<Value>k__BackingField") ?? throw new MissingFieldException(type.Name, "Value");
        FieldInfo changed = AccessTools.Field(type, "ValueChanged") ?? throw new MissingFieldException(type.Name, "ValueChanged");
        foreach (MethodBase method in DeclaredMethods(type))
            foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(method))
            {
                if ((instruction.opcode != OpCodes.Stfld && instruction.opcode != OpCodes.Ldflda) || instruction.operand is not FieldInfo field) continue;
                string[]? allowed = Same(field, value) ? ["set_Value"] : Same(field, changed) ? ["add_ValueChanged", "remove_ValueChanged"] : null;
                if (allowed != null && (method.DeclaringType != type || Array.IndexOf(allowed, method.Name) < 0))
                    throw new NotSupportedException("Unaudited write of " + type.Name + "." + field.Name + " in " + method.DeclaringType?.Name + "." + method.Name);
            }
    }

    private static bool Same(FieldInfo a, FieldInfo b) => a.Module == b.Module && a.MetadataToken == b.MetadataToken;

    private static IEnumerable<MethodBase> DeclaredMethods(Type type)
    {
        const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        foreach (MethodBase method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            if (method.GetMethodBody() != null) yield return method;
        foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (MethodBase method in DeclaredMethods(nested))
                yield return method;
    }

    internal static void Reject(string reason)
    {
        Volatile.Write(ref _ready, 0);
        _frameValidated = false;
        ReleaseAll();
        if (Interlocked.CompareExchange(ref _failure, reason, null) == null) Log.Info("Smoothed value active set disabled: " + reason);
    }

    internal static void LogForeign(string reason)
    {
        if (_loggedForeign) return;
        _loggedForeign = true;
        Log.Info("Smoothed value active set paused (native updates): " + reason);
    }

    internal static void Disable()
    {
        Volatile.Write(ref _ready, 0);
        _frameValidated = false;
        ReleaseAll();
    }
}
