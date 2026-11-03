using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Simulation;
using Halfling;
using Halfling.Timing;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>
/// Single player only. Above x1 vanilla hands the sim <c>speed x frame time</c> of game time per frame
/// (SPManager.Update -> SimRoot.OnInputTick), so at x8 a machine that cannot run the ticks fast enough runs up to eight
/// of them in one frame: fleet-combat drops from ~26 fps at x1 to ~7 at x8.
/// <para/>
/// The game time handed out per frame is now capped to the ticks that fit a wall-time budget at the measured cost per
/// tick, but never below what x1 would get this frame. Budget = what is left of a 1/TargetFps frame after the measured
/// non-tick frame work, and at least <see cref="Share"/> of it. So x8 that fits the frame is unchanged, and x8 that
/// does not runs at most as many ticks per frame as the budget or x1 allow; the time it cannot run is dropped, as
/// vanilla drops slow-frame time. Ticks, their order and inputs are unchanged; only which frame runs a tick moves.
/// Speed x1 and below, pause, debug tick step / full throttle and multiplayer (lockstep pacing comes from the host's
/// input ticks) are untouched. The cap is applied where SPManager.Update (SPManager.cs:72) takes the frame's real delta time
/// from NetManager.GetTargetAdjustedDeltaTime (<see cref="Pacing"/>). Off: <c>UNOFFICIAL_PATCH_TICK_BUDGET=0</c>.
/// </summary>
internal static class TickBudget
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("UNOFFICIAL_PATCH_TICK_BUDGET"));
    internal static bool IsRequested(string? value) => value != "0";

    /// <summary>Least share of a 1/TargetFps frame the ticks may take, however expensive the rest of the frame is.</summary>
    internal const double Share = 0.65;

    /// <summary>Weight of the newest sample in the averages (~10 samples memory: follows a battle starting within a second).</summary>
    internal const double Alpha = 0.1;

    internal static readonly MethodInfo Target = AccessTools.Method(typeof(SimRoot), "FixedUpdate", Type.EmptyTypes)
        ?? throw new MissingMethodException(nameof(SimRoot), "FixedUpdate");

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? [(Target, typeof(TickBudget)), (Pacing.Target, typeof(Pacing))] : [];

    /// <summary>Replaces the single <c>call GetTargetAdjustedDeltaTime</c> in SPManager.Update with <c>call DeltaTime(NetManager)</c>.</summary>
    internal static class Pacing
    {
        private const string IlSha256 = "E32E78E0BB0AF25444AC043CA848567671AA6E389A2C7BCC3601E0653E071480";

        internal static readonly MethodInfo Target = AccessTools.Method(typeof(SPManager), nameof(SPManager.Update))
            ?? throw new MissingMethodException(nameof(SPManager), nameof(SPManager.Update));

        internal static readonly MethodInfo Vanilla = AccessTools.Method(typeof(NetManager), nameof(NetManager.GetTargetAdjustedDeltaTime))
            ?? throw new MissingMethodException(nameof(NetManager), nameof(NetManager.GetTargetAdjustedDeltaTime));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = Patcher.RequireIl(instructions, IlSha256, "SPManager.Update");
            CodeInstruction call = code.Single(ci => ci.Calls(Vanilla));
            call.opcode = OpCodes.Call;
            call.operand = AccessTools.Method(typeof(Pacing), nameof(DeltaTime));
            return code;
        }

        /// <summary>Main thread, once per SP frame, in place of vanilla's call.</summary>
        internal static Time DeltaTime(NetManager net) =>
            Limit(net, net.GetTargetAdjustedDeltaTime()); // exactly once: tick step mode reads keys and prints
    }

    // Main thread only. Averages belong to one SimRoot: a new game (or an MP sim) starts from unknown.
    private static readonly WeakReference<SimRoot?> _sim = new(null);
    private static double _tickCost;   // average wall seconds of one SimRoot.FixedUpdate; 0 = unknown (no cap)
    private static double _otherCost;  // average non-tick work per frame (FrameStarting..FrameEnded minus ticks)
    private static double _ticksInFrame;
    private static long _frameStart;
    private static bool _subscribed;

    public static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

    public static void Postfix(SimRoot __instance, long __state)
    {
        double sample = Seconds(Stopwatch.GetTimestamp() - __state);
        Own(__instance);
        _tickCost = Average(_tickCost, sample);
        _ticksInFrame += sample;
    }

    private static void Own(SimRoot sim)
    {
        if (_sim.TryGetTarget(out SimRoot? owner) && ReferenceEquals(owner, sim)) return;
        _sim.SetTarget(sim);
        _tickCost = _otherCost = 0;
    }

    // Director frame events bracket Update/Input/Draw but not Present, vsync or the frame limiter (they run after the
    // iterator yields, Director.cs:404-406), so waiting is never mistaken for work the ticks cannot use.
    private static void OnFrameStarting(object? sender, EventArgs e)
    {
        _frameStart = Stopwatch.GetTimestamp();
        _ticksInFrame = 0;
    }

    private static void OnFrameEnded(object? sender, EventArgs e)
    {
        if (_frameStart != 0) _otherCost = Average(_otherCost, Math.Max(0, Seconds(Stopwatch.GetTimestamp() - _frameStart) - _ticksInFrame));
    }

    private static double Seconds(long ticks) => (double)ticks / Stopwatch.Frequency;

    internal static double Average(double avg, double sample) => avg > 0 ? avg + Alpha * (sample - avg) : sample;

    /// <summary>Main thread, once per SP frame: caps the real delta time SPManager.Update hands to the sim.</summary>
    internal static double Limit(NetManager net, double vanilla)
    {
        if (!_subscribed)
        {
            _subscribed = true; // the Director lives as long as the app
            App.Director.FrameStarting += OnFrameStarting;
            App.Director.FrameEnded += OnFrameEnded;
        }
        Own(net.Sim);
        if (net.Game.DebugTools is { TickStepMode: true } or { FullThrottle: true }) return vanilla;
        double frame = 1.0 / (int)Settings.TargetFps;
        if (!(frame > 0) || double.IsInfinity(frame)) frame = 1.0 / 60; // FpsTarget0 = uncapped: budget as for 60
        return Cap(vanilla, net.Sim.Clock.ScaleFactor, _tickCost, Budget(frame, _otherCost), 1.0 / net.Sim.Rules.PhysicsUpdatesPerSecond);
    }

    /// <summary>Tick time per frame: the frame left after the non-tick work, at least <see cref="Share"/> of it.</summary>
    internal static double Budget(double frame, double other) => Math.Max(Share * frame, frame - other);

    /// <summary>
    /// Real delta time for this frame: <paramref name="vanilla"/>, but its game time (x <paramref name="scale"/>) at most
    /// the ticks that fit <paramref name="budget"/> at <paramref name="cost"/> each, and never less game time than x1 gets
    /// (<paramref name="vanilla"/> real seconds at speed 1).
    /// </summary>
    internal static double Cap(double vanilla, double scale, double cost, double budget, double interval)
    {
        if (!(scale > 1) || !(cost > 0)) return vanilla;
        double game = Math.Max(vanilla, budget / cost * interval);
        return Math.Min(vanilla, game / scale);
    }
}
