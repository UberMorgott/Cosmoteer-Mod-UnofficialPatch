using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Simulation;
using Halfling;
using Halfling.Application;
using HarmonyLib;

namespace Performance;

internal static class CullCadence
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_CULL_CADENCE"));
    internal static bool IsRequested(string? value) => value == "1";
    private static int _ready;
    private static long _ownedUpdateCalls;
    private static bool _attempted;
    private static string? _failureReason;
    private static WeakReference<SimRoot>? _owner;
    private static Director? _director;
    [ThreadStatic] private static bool _active;
    internal static bool Ready => Volatile.Read(ref _ready) != 0;
    internal static string? FailureReason => Volatile.Read(ref _failureReason);
    internal static long OwnedUpdateCalls => Interlocked.Read(ref _ownedUpdateCalls);
    internal static readonly MethodInfo Target = AccessTools.Method(typeof(SimShipsManager), "FixedUpdate");
    internal static readonly MethodInfo NativeMax = AccessTools.Method(typeof(Mathx), "Max", [typeof(int), typeof(int)]);
    internal static (MethodBase Target, Type Patch)[] Targets => [(Target, typeof(CullCadence))];
    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? Targets : [];
    private static readonly MethodBase[] Dependencies =
    [
        Target, NativeMax,
        AccessTools.PropertyGetter(typeof(SimComponent), nameof(SimComponent.Sim)),
        AccessTools.PropertyGetter(typeof(SimRoot), nameof(SimRoot.Game)),
        AccessTools.PropertyGetter(typeof(GameRoot), nameof(GameRoot.NetManager)),
    ];

    internal static bool Initialize()
    {
        if (!Requested) return true;
        if (_attempted) return Ready;
        _attempted = true;
        try
        {
            Director director = App.Director ?? throw new InvalidOperationException("Cull cadence requires the native Director");
            if (director.FrameCount != 0) throw new InvalidOperationException("Cull cadence requires Director.FrameCount == 0");
            ValidateNative();
            if (Patcher.FindR2RInliners(Target).Count != 0) throw new NotSupportedException("Cull cadence target has precompiled inliners");
            if (!AtlasWriteCoverage.TryDisableInlining(Target, out string? error)) throw new NotSupportedException(error);
            Patches? patches = Harmony.GetPatchInfo(Target);
            if (patches == null || !HasHook(patches.Prefixes, nameof(Prefix)) || !HasHook(patches.Transpilers, nameof(Transpiler)) || !HasHook(patches.Finalizers, nameof(Finalizer)))
                throw new InvalidOperationException("Cull cadence hook coverage missing");
            if (!Compatible()) throw new NotSupportedException("Foreign patch on cull cadence dependency");
            _director = director;
            director.FrameStarting += OnFrameStarting;
            Volatile.Write(ref _ready, 1);
            Log.Info("Cull cadence prototype ready: requires explicit owned SP simulation binding");
            return true;
        }
        catch (Exception ex) { Reject(ex.Message); return false; }
    }

    private static bool HasHook(IEnumerable<Patch> patches, string name) => patches.Any(p =>
        p.owner == Patcher.HarmonyId && p.PatchMethod.DeclaringType == typeof(CullCadence) && p.PatchMethod.Name == name);

    internal static bool BindOwnedSimulation(SimRoot sim)
    {
        if (!Requested || !Ready) return false;
        try
        {
            if (!Compatible()) throw new NotSupportedException("Foreign patch on cull cadence dependency");
            WeakReference<SimRoot>? reference = Volatile.Read(ref _owner);
            bool sameOwner = false;
            if (reference != null && reference.TryGetTarget(out SimRoot? current))
            {
                if (!ReferenceEquals(current, sim)) return false;
                sameOwner = true;
            }
            if (sim.Game?.NetManager is not SPManager) throw new NotSupportedException("Cull cadence requires an explicitly owned single-player simulation");
            if (sameOwner) return true;
            if (_active) throw new InvalidOperationException("Cannot bind cull cadence during an active owned update");
            Volatile.Write(ref _owner, new WeakReference<SimRoot>(sim));
            Log.Info("Cull cadence prototype bound to owned SP simulation");
            return true;
        }
        catch (Exception ex) { Reject(ex.Message); return false; }
    }

    private static bool Compatible()
    {
        foreach (MethodBase method in Harmony.GetAllPatchedMethods())
        {
            if (!Dependencies.Any(dependency => BlueprintToggleEvaluation.SameMethodDefinition(method, dependency))) continue;
            Patches? patches = Harmony.GetPatchInfo(method);
            if (patches == null || patches.Owners.Any(owner => owner != Patcher.HarmonyId)) return false;
        }
        return true;
    }

    internal static void CheckCompatibility()
    {
        if (!Ready) return;
        try { if (!Compatible()) Reject("Foreign patch on cull cadence dependency"); }
        catch (Exception ex) { Reject(ex.Message); }
    }
    private static void OnFrameStarting(object? sender, EventArgs e) => CheckCompatibility();

    private static void Reject(string reason)
    {
        Interlocked.CompareExchange(ref _failureReason, reason, null);
        Disable();
        Log.Info("Cull cadence prototype disabled: " + FailureReason);
    }
    internal static void Disable()
    {
        Volatile.Write(ref _ready, 0);
        Volatile.Write(ref _owner, null);
        _active = false;
        if (_director == null) return;
        _director.FrameStarting -= OnFrameStarting;
        _director = null;
    }

    internal static int Bound(int period, int count)
    {
        bool active = _active && Ready;
        if (active && period > 0) Interlocked.Increment(ref _ownedUpdateCalls);
        return SelectBound(active, period, count);
    }
    internal static int SelectBound(bool active, int period, int count) => active && period > 0 ? period : Mathx.Max(period, count);

    private static void ValidateNative()
    {
        if (Target.Module.ModuleVersionId != new Guid("fd7c0bba-64d3-40b4-a571-bf8b34eb4277") ||
            Convert.ToHexString(SHA256.HashData(Target.GetMethodBody()!.GetILAsByteArray()!)) != "A8EC5E5CC583D38EFE711C88AAE8EAC75996E0B1E44ECE5B477548A0B7F61464" ||
            NativeMax.Module.ModuleVersionId != new Guid("29d04acb-ae49-4f3b-8499-f03c9144a3a9") ||
            Convert.ToHexString(SHA256.HashData(NativeMax.GetMethodBody()!.GetILAsByteArray()!)) != "8AF9ECFA2C40E4A589552168379E897E75E67190AC6866CF6462C4B997357A57")
            throw new InvalidOperationException("Cull cadence native IL/MVID changed");
    }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        ValidateNative();
        List<CodeInstruction> code = Patcher.RequireIl(instructions, "1551ABA44F8B2D62004F0392509F196716FBDA187442DB19BBF8EB6BB5AB8419", "SimShipsManager.FixedUpdate");
        var calls = code.Where(instruction => instruction.Calls(NativeMax)).ToArray();
        if (calls.Length != 1) throw new InvalidOperationException("Cull cadence Max call site changed");
        calls[0].operand = AccessTools.Method(typeof(CullCadence), nameof(Bound));
        return code;
    }
    public static void Prefix(SimShipsManager __instance, out bool __state)
    {
        __state = _active;
        _active = false;
        if (!Ready) return;
        try
        {
            WeakReference<SimRoot>? reference = Volatile.Read(ref _owner);
            if (reference != null && reference.TryGetTarget(out SimRoot? owner) &&
                ReferenceEquals(__instance.Sim, owner) && owner.Game?.NetManager is SPManager) _active = true;
        }
        catch (Exception ex) { Reject(ex.Message); }
    }
    public static Exception? Finalizer(bool __state, Exception? __exception)
    {
        _active = __state;
        return __exception;
    }
}
