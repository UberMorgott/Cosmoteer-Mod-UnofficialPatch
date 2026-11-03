using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer;
using Cosmoteer.Game.Multiplayer;
using Halfling;
using Halfling.Timing;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>
/// Single player only. Every frame SPManager.Update (SPManager.cs:72) takes the real time the sim may advance this frame
/// from NetManager.GetTargetAdjustedDeltaTime (NetManager.cs:372-375): the frame time, but at most
/// <c>cap = min(1/TargetFps, 1/PhysicsUpdatesPerSecond)</c>, and for a frame slower than 1/TargetFps only
/// <c>T^2/dt</c>. Whatever it does not hand out is dropped, so every slow frame (GC pause, heavy tick batch) loses game
/// time for good: at x8 in a 40-ship scene 3.6% of real time (231 instead of 240 ticks/s).
/// <para/>
/// The dropped part is now kept as debt (at most <see cref="MaxDebt"/> s, older debt is dropped as before) and handed
/// out on later frames, but no frame ever gets more than <c>cap</c>, the most vanilla can give a frame. So no frame runs
/// more ticks than vanilla's own worst case (x8: 8), the backlog is bounded, and with no debt the value is exactly
/// vanilla's. Ticks and their inputs are unchanged; only which frame runs a tick moves (as between faster and slower
/// machines). Under sustained overload more frames carry their full tick allowance, so the sim runs faster than vanilla
/// at the cost of frame rate (fleet-combat x1: 18.0 vs 15.2 ticks/s, 26 vs 36 fps); when even that is not enough the
/// game still slows down and debt beyond the bound is dropped. Hence opt-in. Debug tick step / full throttle, pause, a
/// game speed change (debt is real time; OnInputTick multiplies it by the new speed) and a new SPManager reset the debt
/// and stay vanilla. Multiplayer, RecordableSPManager and GetTargetAdjustedDeltaTime itself are not patched.
/// On: <c>UNOFFICIAL_PATCH_SP_CATCHUP=1</c>.
/// </summary>
internal static class SpCatchUp
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("UNOFFICIAL_PATCH_SP_CATCHUP"));
    internal static bool IsRequested(string? value) => value == "1";

    /// <summary>
    /// Real seconds of dropped frame time kept for later frames. Must outlast an overload phase: seeded40-far-x8 starts
    /// with ~2.5 s of 40 ms frames already at the 8-tick cap (0.43 s of debt); 0.25 s dropped 40 ticks of it.
    /// </summary>
    internal const double MaxDebt = 1.0;

    private const string IlSha256 = "E32E78E0BB0AF25444AC043CA848567671AA6E389A2C7BCC3601E0653E071480";

    internal static readonly MethodInfo Target = AccessTools.Method(typeof(SPManager), nameof(SPManager.Update))
        ?? throw new MissingMethodException(nameof(SPManager), nameof(SPManager.Update));

    internal static readonly MethodInfo Vanilla = AccessTools.Method(typeof(NetManager), nameof(NetManager.GetTargetAdjustedDeltaTime))
        ?? throw new MissingMethodException(nameof(NetManager), nameof(NetManager.GetTargetAdjustedDeltaTime));

    /// <summary>Also carries <see cref="TickBudget"/>'s cap: both rewrite the same call.</summary>
    internal static (MethodBase Target, Type Patch)[] Patches => Requested || TickBudget.Requested ? [(Target, typeof(SpCatchUp))] : [];

    private static readonly WeakReference<NetManager?> _owner = new(null); // never keeps a finished game alive
    private static float _scale;
    private static double _debt;

    /// <summary>Replaces the single <c>call GetTargetAdjustedDeltaTime</c> with <c>call DeltaTime(NetManager)</c>.</summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = Patcher.RequireIl(instructions, IlSha256, "SPManager.Update");
        CodeInstruction call = code.Single(ci => ci.Calls(Vanilla));
        call.opcode = OpCodes.Call;
        call.operand = AccessTools.Method(typeof(SpCatchUp), nameof(DeltaTime));
        return code;
    }

    /// <summary>Main thread, once per SP frame, in place of vanilla's call.</summary>
    internal static Time DeltaTime(NetManager net)
    {
        Time vanilla = net.GetTargetAdjustedDeltaTime(); // exactly once: tick step mode reads keys and prints
        double proposed = Requested ? CatchUp(net, vanilla) : vanilla;
        return TickBudget.Requested ? TickBudget.Limit(net, vanilla, proposed) : proposed;
    }

    private static double CatchUp(NetManager net, Time vanilla)
    {
        var debug = net.Game.DebugTools;
        float scale = net.Sim.Clock.ScaleFactor;
        bool sameOwner = _owner.TryGetTarget(out NetManager? owner) && ReferenceEquals(net, owner);
        if (MustReset(sameOwner, scale, _scale, debug is { TickStepMode: true } or { FullThrottle: true }))
        {
            _owner.SetTarget(net);
            _scale = scale;
            _debt = 0;
            return vanilla;
        }
        double cap = Math.Min(1f / (float)Settings.TargetFps, 1f / net.Sim.Rules.PhysicsUpdatesPerSecond);
        return Step(ref _debt, App.Clock.DeltaTime, vanilla, cap);
    }

    /// <summary>Debt is dropped (vanilla value used) on a new SPManager, a speed change, pause or a debug pacing mode.</summary>
    internal static bool MustReset(bool sameOwner, float scale, float lastScale, bool debugPacing) =>
        !sameOwner || !scale.Equals(lastScale) || debugPacing || !(scale > 0f);

    /// <summary>
    /// Adds what vanilla dropped this frame to the debt (bounded), then hands out debt up to the per-frame cap.
    /// Returns vanilla unchanged while there is no debt.
    /// </summary>
    internal static double Step(ref double debt, double dt, double vanilla, double cap)
    {
        if (!(dt > vanilla)) dt = vanilla; // vanilla never exceeds dt; guard NaN/odd clocks
        debt = Math.Min(debt + (dt - vanilla), MaxDebt);
        double extra = Math.Min(debt, Math.Max(0, cap - vanilla));
        debt -= extra;
        return vanilla + extra;
    }
}
