using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Keeps this mod's Harmony patches on shared generic code (a method of <c>Foo&lt;RefType&gt;</c>) installed after the runtime
/// recompiles that code (tiered compilation: R2R or tier-0 code is replaced by tier-1 code after ~30 calls).
/// <para/>
/// Root cause (installed MonoMod.Core in the loader's 0Harmony): MonoMod re-applies a detour when the JIT reports a new body
/// for the detoured method, looking the method up in <c>PlatformTripleDetourFactory.Detour.relatedDetours</c>, which is keyed
/// by the patched MethodBase (<c>Foo&lt;Part&gt;.M</c>). For shared code the JIT reports the canonical method
/// (<c>Foo&lt;__Canon&gt;.M</c>), so the lookup misses and the new tier-1 body runs vanilla: the patch silently stops working.
/// <para/>
/// This adds one more listener to the same MonoMod compile event. It acts only for the shared generic targets
/// <see cref="Patcher"/> registered (families): when the JIT reports a family's canonical method and that family's patched
/// method is the only detour key sharing the code, the key is handed to MonoMod's own retarget handler, so the existing detour,
/// its generic-context thunk and unpatching work exactly as for non-generic methods. Other mods' detours are never forwarded.
/// Another detour on the same shared code (alias: another instantiation, or the canonical method itself) or a failing retarget
/// marks the family failed instead; <see cref="Patcher.ApplyRetargetFallback"/> then unpatches it on the next frame, outside the
/// JIT callback. Pinned to the loader's 0Harmony build (MonoMod is merged into it); when the build or any piece used here
/// differs, <see cref="Enable"/> fails and <see cref="Patcher"/> leaves every shared generic target unpatched (vanilla).
/// </summary>
internal static class SharedGenericRetarget
{
    internal delegate void Compiled(RuntimeMethodHandle handle, MethodBase? method, IntPtr codeStart, IntPtr codeRw, ulong codeSize);

    /// <summary>Immutable once published; JIT-thread readers take it with one volatile read.</summary>
    private sealed class State(MethodBase[] families, IDictionary related, Compiled retarget)
    {
        internal readonly MethodBase[] Families = families;
        internal readonly int[] FailedFlags = new int[families.Length];
        internal readonly IDictionary Related = related;
        internal readonly Compiled Retarget = retarget;
    }

    private static readonly Type? Canon = typeof(object).Assembly.GetType("System.__Canon");
    private static readonly object Gate = new();
    private static State? _state;
    private static (EventInfo Event, object Runtime, Delegate Handler)? _subscription;
    private static readonly ConcurrentQueue<MethodBase> FailedFamilies = new();
    private static int _failed;
    private static int _retargets;

    /// <summary>Canonical bodies handed to MonoMod's retarget handler so far (diagnostics and tests).</summary>
    internal static int Retargets => Volatile.Read(ref _retargets);

    internal static bool IsEnabled => Volatile.Read(ref _state) != null;

    /// <summary>A family hit an alias or a failed retarget; its patch must be removed (<see cref="TakeFailed"/>).</summary>
    internal static bool Failed => Volatile.Read(ref _failed) != 0;

    /// <summary>True when the method runs shared generic code (a reference type, also nested, in its type or method arguments).</summary>
    internal static bool IsShared(MethodBase method)
    {
        if (method.DeclaringType is { IsGenericType: true } type && AnyShared(type.GetGenericArguments())) return true;
        return method.IsGenericMethod && AnyShared(method.GetGenericArguments());
    }

    private static bool AnyShared(Type[] args)
    {
        foreach (Type arg in args)
            if (IsSharedArgument(arg)) return true;
        return false;
    }

    private static bool IsSharedArgument(Type arg) =>
        !arg.IsValueType || (arg.IsGenericType && AnyShared(arg.GetGenericArguments()));

    /// <summary>
    /// Subscribes the listener for <paramref name="families"/> (this mod's shared generic targets). Already enabled: the
    /// families must be the same. False with <paramref name="error"/> when the installed MonoMod is not the pinned one or two
    /// families share code.
    /// </summary>
    internal static bool Enable(IReadOnlyList<MethodBase> families, out string? error)
    {
        lock (Gate)
        {
            error = null;
            try
            {
                MethodBase[] registered = ToFamilies(families);
                if (Volatile.Read(ref _state) is { } current)
                {
                    if (current.Families.Length == registered.Length && Array.TrueForAll(registered, f => Array.IndexOf(current.Families, f) >= 0)) return true;
                    throw new InvalidOperationException("already enabled for other shared generic targets");
                }
                if (_subscription != null) throw new InvalidOperationException("previous listener could not be removed");
                HarmonyPatchIdentity.ValidateRuntime();
                if (Canon == null) throw new NotSupportedException("System.__Canon missing");
                Assembly monoMod = typeof(Harmony).Assembly;
                Type triple = Require(monoMod.GetType("MonoMod.Core.Platforms.PlatformTriple"), "PlatformTriple");
                Type runtimeType = Require(monoMod.GetType("MonoMod.Core.Platforms.IRuntime"), "IRuntime");
                Type callback = Require(monoMod.GetType("MonoMod.Core.Platforms.OnMethodCompiledCallback"), "OnMethodCompiledCallback");
                Type detour = Require(monoMod.GetType("MonoMod.Core.Platforms.PlatformTripleDetourFactory+Detour"), "PlatformTripleDetourFactory.Detour");
                object platform = Require(triple.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null), "PlatformTriple.Current");
                object runtime = Require(triple.GetProperty("Runtime", BindingFlags.Public | BindingFlags.Instance)?.GetValue(platform), "PlatformTriple.Runtime");
                EventInfo compiled = Require(runtimeType.GetEvent("OnMethodCompiled"), "IRuntime.OnMethodCompiled");
                if (compiled.EventHandlerType != callback) throw new NotSupportedException("IRuntime.OnMethodCompiled type changed");
                MethodInfo handler = Require(detour.GetMethod("OnMethodCompiled", BindingFlags.NonPublic | BindingFlags.Static, null,
                    [typeof(RuntimeMethodHandle), typeof(MethodBase), typeof(IntPtr), typeof(IntPtr), typeof(ulong)], null), "Detour.OnMethodCompiled");
                FieldInfo related = Require(detour.GetField("relatedDetours", BindingFlags.NonPublic | BindingFlags.Static), "Detour.relatedDetours");
                if (!related.FieldType.IsGenericType || related.FieldType.GetGenericTypeDefinition() != typeof(ConcurrentDictionary<,>)
                    || related.FieldType.GetGenericArguments()[0] != typeof(MethodBase) || !related.IsInitOnly)
                    throw new NotSupportedException("Detour.relatedDetours type changed");
                IDictionary relatedDetours = Require(related.GetValue(null) as IDictionary, "Detour.relatedDetours value");
                MethodInfo listener = typeof(SharedGenericRetarget).GetMethod(nameof(OnCompiled), BindingFlags.NonPublic | BindingFlags.Static)!;
                // Compiled up front: the listener runs inside the JIT hook.
                foreach (string name in new[] { nameof(OnCompiled), nameof(Forward), nameof(SharesCode), nameof(Matches), nameof(ContainsCanon), nameof(MarkFailed) })
                    RuntimeHelpers.PrepareMethod(typeof(SharedGenericRetarget).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.MethodHandle);
                Delegate subscriber = Delegate.CreateDelegate(callback, listener);
                Volatile.Write(ref _state, new State(registered, relatedDetours, handler.CreateDelegate<Compiled>()));
                compiled.AddEventHandler(runtime, subscriber);
                _subscription = (compiled, runtime, subscriber);
                return true;
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _state, null);
                error = "shared generic retarget unsupported: " + (ex.InnerException ?? ex).Message;
                return false;
            }
        }
    }

    /// <summary>Test seam: the listener logic over a given detour registry and retarget handler, without subscribing.</summary>
    internal static void EnableForTest(IReadOnlyList<MethodBase> families, IDictionary related, Compiled retarget)
    {
        lock (Gate)
        {
            if (_subscription != null || Volatile.Read(ref _state) != null) throw new InvalidOperationException("listener already enabled");
            Volatile.Write(ref _state, new State(ToFamilies(families), related, retarget));
        }
    }

    private static MethodBase[] ToFamilies(IReadOnlyList<MethodBase> families)
    {
        var result = new MethodBase[families.Count];
        for (int i = 0; i < result.Length; i++)
        {
            MethodBase family = families[i];
            if (!IsShared(family) || family.ContainsGenericParameters) throw new ArgumentException("not a closed shared generic method: " + family);
            for (int j = 0; j < i; j++)
                if (SameDefinition(result[j], family)) throw new ArgumentException($"two targets share code: {result[j]} and {family}");
            result[i] = family;
        }
        return result;
    }

    // Conservative: same method definition always counts as possibly shared code.
    private static bool SameDefinition(MethodBase a, MethodBase b) => a.MetadataToken == b.MetadataToken && a.Module == b.Module;

    /// <summary>
    /// Stops forwarding first (callbacks already running finish, later ones see no state), then unsubscribes. False when the
    /// listener could not be removed (it stays inert; <see cref="Enable"/> then refuses).
    /// </summary>
    internal static bool Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref _state, null);
            if (_subscription is not { } s) return true;
            try
            {
                s.Event.RemoveEventHandler(s.Runtime, s.Handler);
            }
            catch (Exception ex)
            {
                Log.Error("retarget-disable", ex);
                return false;
            }
            _subscription = null;
            return true;
        }
    }

    /// <summary>Failed families since the last call (main thread, outside JIT callbacks); clears <see cref="Failed"/>.</summary>
    internal static List<MethodBase> TakeFailed()
    {
        Volatile.Write(ref _failed, 0);
        var result = new List<MethodBase>();
        while (FailedFamilies.TryDequeue(out MethodBase? family)) result.Add(family);
        return result;
    }

    private static T Require<T>(T? value, string what) where T : class =>
        value ?? throw new NotSupportedException("MonoMod changed: " + what + " missing");

    /// <summary>MonoMod compile event: runs on the compiling thread, after MonoMod's own (exact-key) handler. Takes no lock of its own.</summary>
    internal static void OnCompiled(RuntimeMethodHandle handle, MethodBase? method, IntPtr codeStart, IntPtr codeRw, ulong codeSize)
    {
        if (method == null || Volatile.Read(ref _state) is not { } state || !IsCanonical(method)) return;
        for (int i = 0; i < state.Families.Length; i++)
        {
            if (Volatile.Read(ref state.FailedFlags[i]) == 0 && SharesCode(state.Families[i], method))
                Forward(state, i, handle, method, codeStart, codeRw, codeSize);
        }
    }

    private static void Forward(State state, int index, RuntimeMethodHandle handle, MethodBase canonical, IntPtr codeStart, IntPtr codeRw, ulong codeSize)
    {
        MethodBase family = state.Families[index];
        try
        {
            bool ours = false;
            int aliases = 0;
            foreach (object key in state.Related.Keys) // snapshot; one entry per detoured method
            {
                if (key is not MethodBase detoured || !SharesCode(detoured, canonical)) continue;
                if (detoured.DeclaringType == family.DeclaringType && (!family.IsGenericMethod || AllMatch(family.GetGenericArguments(), detoured.GetGenericArguments()))) ours = true;
                else aliases++;
            }
            if (!ours) return; // not patched now: nothing of this mod on that code
            if (aliases != 0)
            {
                MarkFailed(state, index, family);
                return;
            }
            if (Volatile.Read(ref _state) != state) return; // disabled meanwhile
            Interlocked.Increment(ref _retargets);
            state.Retarget(handle, family, codeStart, codeRw, codeSize);
        }
        catch (Exception)
        {
            MarkFailed(state, index, family);
        }
    }

    private static void MarkFailed(State state, int index, MethodBase family)
    {
        if (Interlocked.Exchange(ref state.FailedFlags[index], 1) != 0) return;
        FailedFamilies.Enqueue(family);
        Volatile.Write(ref _failed, 1);
    }

    private static bool IsCanonical(MethodBase method) =>
        (method.DeclaringType is { IsGenericType: true } type && ContainsCanon(type)) || (method.IsGenericMethod && AnyCanon(method.GetGenericArguments()));

    private static bool ContainsCanon(Type type)
    {
        if (type == Canon) return true;
        if (type.HasElementType) return ContainsCanon(type.GetElementType()!);
        return type.IsGenericType && AnyCanon(type.GetGenericArguments());
    }

    private static bool AnyCanon(Type[] args)
    {
        foreach (Type arg in args)
            if (ContainsCanon(arg)) return true;
        return false;
    }

    /// <summary>The patched instantiation runs the reported canonical code: same method definition, matching arguments.</summary>
    internal static bool SharesCode(MethodBase patched, MethodBase canonical)
    {
        if (!SameDefinition(patched, canonical)) return false;
        if (patched.IsGenericMethod != canonical.IsGenericMethod) return false;
        if (patched.DeclaringType is not { } pt || canonical.DeclaringType is not { } ct || !Matches(ct, pt)) return false;
        return !patched.IsGenericMethod || AllMatch(canonical.GetGenericArguments(), patched.GetGenericArguments());
    }

    /// <summary>Exact type, or __Canon standing for a reference type, recursively through generic arguments and element types.</summary>
    private static bool Matches(Type canonical, Type concrete)
    {
        if (canonical == concrete) return true;
        if (canonical == Canon) return !concrete.IsValueType && !concrete.IsPointer && !concrete.IsByRef;
        if (canonical.IsArray)
            return concrete.IsArray && canonical.GetArrayRank() == concrete.GetArrayRank() && canonical.IsSZArray == concrete.IsSZArray
                && Matches(canonical.GetElementType()!, concrete.GetElementType()!);
        if (!canonical.IsGenericType || !concrete.IsGenericType || canonical.GetGenericTypeDefinition() != concrete.GetGenericTypeDefinition()) return false;
        return AllMatch(canonical.GetGenericArguments(), concrete.GetGenericArguments());
    }

    private static bool AllMatch(Type[] canonical, Type[] concrete)
    {
        if (canonical.Length != concrete.Length) return false;
        for (int i = 0; i < canonical.Length; i++)
            if (!Matches(canonical[i], concrete[i])) return false;
        return true;
    }
}
