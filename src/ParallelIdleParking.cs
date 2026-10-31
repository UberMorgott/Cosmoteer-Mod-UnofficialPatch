using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using Halfling;
using Halfling.Application;
using Halfling.Performance;
using HarmonyLib;

namespace Performance;

internal static class ParallelIdleParking
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_IDLE_PARK"));
    internal static bool IsRequested(string? value) => value == "1";
    private static readonly IdleGate Idle = new(static () => FastParallel.IsRunning, 8, 10);
    private static readonly Type PoolType = AccessTools.Field(typeof(FastParallel), "s_liveTasks").FieldType.GetGenericTypeDefinition();
    private static bool _attempted;
    private static Director? _director;
    private static string? _failureReason;
    internal static bool Ready => Idle.IsEnabled;
    internal static string? FailureReason => Volatile.Read(ref _failureReason);
    internal static long ParkCalls => Idle.ParkCount;
    internal static long WakeSignals => Idle.SignalCount;
    internal static readonly MethodInfo RunThreadTarget = AccessTools.Method(typeof(FastParallel), "RunThread");
    internal static readonly (MethodBase Target, Type Patch)[] Targets =
    [
        (RunThreadTarget, typeof(WorkerHook)),
        (AccessTools.Method(typeof(FastParallel), "AddToLive"), typeof(SignalHook)),
        (AccessTools.Method(typeof(FastParallel), "Start"), typeof(SignalHook)),
        (AccessTools.Method(typeof(FastParallel), "Stop"), typeof(SignalHook)),
    ];
    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? Targets : [];
    private static readonly string[] Pins =
    [
        "1D136C58B12B26B0D49B2C1932901C9F85FBD31AE08B55BB11BED665FDF0308D",
        "1B8327E99E45DBE8792068D52C4C954383F28270F95151071205967975CD1776",
        "A8C9E54C7CA11D68D7B4D115F76759D4541A7D3F679006C56BEF25DC470F42AD",
        "5C9CAEA6CB1309E06701999439C9BD8D742038E7222C61D61024593F975FB18D",
    ];

    internal static List<CodeInstruction> RequireIl(MethodBase target, IEnumerable<CodeInstruction> instructions)
    {
        for (int i = 0; i < Targets.Length; i++)
            if (BlueprintToggleEvaluation.SameMethodDefinition(target, Targets[i].Target))
                return Patcher.RequireIl(instructions, Pins[i], "FastParallel." + target.Name);
        throw new InvalidOperationException("Unknown idle parking target " + target);
    }

    internal static bool Initialize()
    {
        if (!Requested) return true;
        if (_attempted) return Ready;
        _attempted = true;
        try
        {
            // A body already inlined into a running forever-worker cannot be repaired at this point.
            Director director = App.Director ?? throw new InvalidOperationException("Idle parking requires the native Director");
            if (director.FrameCount != 0) throw new InvalidOperationException("Idle parking requires Director.FrameCount == 0");
            if (FastParallel.s_createdThreads) throw new InvalidOperationException("FastParallel workers already created");
            using (FileStream file = File.OpenRead(typeof(FastParallel).Assembly.Location))
                if (Convert.ToHexString(SHA256.HashData(file)) != "80213C7C8182CE63B9075A48DFD80A7614B1FA2AB3BCD16A69D5363CF9A4B391")
                    throw new InvalidOperationException("FastParallel dependency binary changed");
            foreach ((MethodBase target, Type patch) in Targets)
            {
                RequireIl(target, PatchProcessor.GetOriginalInstructions(target));
                if (Patcher.FindR2RInliners(target).Count != 0)
                    throw new NotSupportedException("FastParallel target has precompiled inliners: " + target);
                if (!Patcher.TryDisableInlining(target, out string? error)) throw new NotSupportedException(error);
                Patches? hooks = Harmony.GetPatchInfo(target);
                if (hooks == null || !hooks.Transpilers.Any(p => p.owner == Patcher.HarmonyId && p.PatchMethod.DeclaringType == patch) ||
                    (patch == typeof(SignalHook) && !hooks.Finalizers.Any(p => p.owner == Patcher.HarmonyId && p.PatchMethod.DeclaringType == patch)))
                    throw new InvalidOperationException("Idle parking hook coverage missing: " + target);
            }
            if (!Compatible()) throw new InvalidOperationException("Foreign patch on FastParallel dependency");
            _director = director;
            _director.FrameStarting += OnFrameStarting;
            Idle.Enable();
            Log.Info("Parallel idle parking active: epoch-notified outer worker wait (PERFORMANCE_IDLE_PARK=1)");
            return true;
        }
        catch (Exception ex)
        {
            Reject(ex.Message);
            return false;
        }
    }

    internal static bool IsDependency(MethodBase method)
    {
        for (Type? type = method.DeclaringType; type != null; type = type.DeclaringType)
            if (type == typeof(FastParallel) || (type.IsGenericType && type.GetGenericTypeDefinition() == PoolType) || type == typeof(SpinWait)) return true;
        return method.DeclaringType == typeof(Director) && method.Name == "get_FrameCount";
    }

    private static bool Compatible()
    {
        foreach (MethodBase method in Harmony.GetAllPatchedMethods())
        {
            if (!IsDependency(method)) continue;
            Patches? patches = Harmony.GetPatchInfo(method);
            if (patches == null || patches.Owners.Any(owner => owner != Patcher.HarmonyId)) return false;
        }
        return true;
    }

    internal static void CheckCompatibility()
    {
        if (!Ready) return;
        try
        {
            if (!Compatible()) Reject("Foreign patch on FastParallel dependency");
        }
        catch (Exception ex) { Reject(ex.Message); }
    }

    private static void OnFrameStarting(object? sender, EventArgs e) => CheckCompatibility();

    private static void Reject(string reason)
    {
        Interlocked.CompareExchange(ref _failureReason, reason, null);
        Disable();
        Log.Info("Parallel idle parking disabled: " + FailureReason);
    }

    internal static void Disable()
    {
        // Disarm and wake BEFORE hook removal. No callback, native queue operation or logging while Gate is held.
        Idle.Disarm();
        if (_director == null) return;
        _director.FrameStarting -= OnFrameStarting;
        _director = null;
    }

    internal static long CaptureEpoch() => Idle.ReadEpoch();
    internal static void Wait(ref SpinWait spin, int threshold, long epoch) => Idle.WaitForPublication(ref spin, threshold, epoch);

    internal static class WorkerHook
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            List<CodeInstruction> code = RequireIl(RunThreadTarget, instructions);
            MethodInfo scan = AccessTools.Method(typeof(FastParallel), "RunUntilEmpty");
            MethodInfo spin = AccessTools.Method(typeof(SpinWait), nameof(SpinWait.SpinOnce), [typeof(int)]);
            int scanIndex = code.FindIndex(i => i.Calls(scan));
            if (code.Count(i => i.Calls(scan)) != 1 || code.Count(i => i.Calls(spin)) != 1 || scanIndex < 2 ||
                code[scanIndex - 2].opcode != OpCodes.Ldarg_0 || code[scanIndex - 1].opcode != OpCodes.Ldloc_0)
                throw new InvalidOperationException("FastParallel outer scan/spin shape changed");
            LocalBuilder epoch = generator.DeclareLocal(typeof(long));
            var capture = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ParallelIdleParking), nameof(CaptureEpoch)));
            capture.MoveLabelsFrom(code[scanIndex - 2]);
            capture.MoveBlocksFrom(code[scanIndex - 2]);
            code.InsertRange(scanIndex - 2, [capture, new CodeInstruction(OpCodes.Stloc, epoch)]);
            int spinIndex = code.FindIndex(i => i.Calls(spin));
            code[spinIndex].opcode = OpCodes.Ldloc;
            code[spinIndex].operand = epoch;
            code.Insert(spinIndex + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ParallelIdleParking), nameof(Wait))));
            return code;
        }
    }
    internal static class SignalHook
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) => RequireIl(__originalMethod, instructions);
        public static Exception? Finalizer(Exception? __exception)
        {
            // Native locks have been released on normal publication; a spurious notification on failure is harmless.
            Idle.Signal();
            return __exception;
        }
    }

    /// <summary>Epoch predicate and notification share a monitor, closing publication-before-wait lost wakeups.</summary>
    internal sealed class IdleGate(Func<bool> isRunning, int warmup, int timeout)
    {
        private readonly object _gate = new();
        private int _ready;
        private long _epoch, _parks, _signals;
        internal bool IsEnabled => Volatile.Read(ref _ready) != 0;
        internal long ParkCount => Interlocked.Read(ref _parks);
        internal long SignalCount => Interlocked.Read(ref _signals);
        internal long ReadEpoch() => Volatile.Read(ref _epoch);
        internal void Enable() { lock (_gate) Volatile.Write(ref _ready, 1); }
        internal void Disarm()
        {
            lock (_gate)
            {
                Volatile.Write(ref _ready, 0);
                Volatile.Write(ref _epoch, _epoch + 1);
                Monitor.PulseAll(_gate);
            }
        }
        internal void Signal()
        {
            lock (_gate)
            {
                Volatile.Write(ref _epoch, _epoch + 1);
                Interlocked.Increment(ref _signals);
                Monitor.PulseAll(_gate);
            }
        }
        internal void WaitForPublication(ref SpinWait spin, int threshold, long epoch)
        {
            if (!IsEnabled || spin.Count < warmup)
            {
                spin.SpinOnce(threshold);
                return;
            }
            bool fallback;
            lock (_gate)
            {
                fallback = !IsEnabled;
                if (!fallback && epoch == _epoch && isRunning())
                {
                    Interlocked.Increment(ref _parks);
                    Monitor.Wait(_gate, timeout);
                }
            }
            if (fallback) spin.SpinOnce(threshold);
        }
    }
}
