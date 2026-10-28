using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cosmoteer.Game.Multiplayer;
using Halfling.Network;
using Halfling.Timing;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Vanilla freeze: the host sets the MP input delay from the two SMALLEST connection latencies (mean of the last 20
/// pings), so one far or jittery player gets less delay than its route needs; its late inputs stall the whole lockstep sim.
/// The prefix sizes the delay for the two LARGEST connections instead, each by the worst of its last 20 pings (pings are
/// reliable, so a sample already includes resend cost), keeping vanilla's loss term, latency factor and clamp. The delay
/// rises at once and drops by one tick only after a calm 5 s with fresh samples. Host-only: clients get the value through
/// vanilla HostUpdate, so all peers stay in lockstep. Vanilla handles delay changes without gaps (_nextInputTickToSend).
/// </summary>
internal static class InputDelay
{
    private const string IlSha256 = "EBAE19E9B473D65BB024EB0CBAB9255F7D366EAB0B2B14D258A0BFF61F790337";
    private const int MinSamples = 5; // below it a connection keeps vanilla's estimate and blocks lowering
    private static readonly long HoldTicks = Stopwatch.Frequency * 5; // elapsed time: completed ticks stall and catch up

    private static readonly ConditionalWeakTable<MPHostManager, Hysteresis> StateByHost = new();

    /// <summary>Last host computation, for <see cref="NetTelemetry"/>'s summary.</summary>
    internal static (int Vanilla, int Target, int Applied) Last;

    internal static MethodInfo Target =>
        AccessTools.Method(typeof(MPHostManager), "CalculateInputTickDelay")
        ?? throw new MissingMethodException(nameof(MPHostManager), "CalculateInputTickDelay");

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        Patcher.RequireIl(instructions, IlSha256, "MPHostManager.CalculateInputTickDelay");

    public static bool Prefix(MPHostManager __instance, ref int __result)
    {
        var rules = __instance.Rules;
        Span<double> worst = stackalloc double[2], best = stackalloc double[2];
        worst.Fill(double.NaN);
        best.Fill(double.NaN);
        bool canLower = true;
        int count = 0;
        foreach (RemoteConnection connection in __instance._connections)
        {
            count++;
            float loss = connection.CurrentUnreliableMessageLossRate;
            double mean = Estimate(connection.Latency, loss);
            Insert(best, mean, largest: false);
            if (connection is NetworkMessenger.InternalRemoteConnection irc && irc._returnedPings.Count >= MinSamples)
            {
                double max = 0;
                foreach (Time ping in irc._returnedPings)
                {
                    max = Math.Max(max, ping);
                }
                // Overdue ping = the window may be hiding a spike in progress.
                canLower &= irc.HasReceivedPingReturn || irc.LocalMessenger.ClockTime - irc.LastPingSentTime <= max;
                Insert(worst, Estimate(max, loss), largest: true);
            }
            else
            {
                canLower = false;
                Insert(worst, mean, largest: true);
            }
        }
        Hysteresis state = StateByHost.GetValue(__instance, _ => new Hysteresis());
        int target = Ticks(Sum(worst) * rules.InputTickDelayLatencyFactor, rules.InputTicksPerSecond, rules.MinInputTickDelay, rules.MaxInputTickDelay);
        __result = state.Next(target, canLower, count, Stopwatch.GetTimestamp(), HoldTicks);
        Last = (Ticks(Sum(best) * rules.InputTickDelayLatencyFactor, rules.InputTicksPerSecond, rules.MinInputTickDelay, rules.MaxInputTickDelay), target, __result);
        return false;
    }

    /// <summary>Vanilla's latency / (1 - loss); total loss saturates to infinity (= max delay), not to NaN.</summary>
    internal static double Estimate(double rtt, float loss) => loss >= 1f ? double.PositiveInfinity : rtt / (1f - loss);

    /// <summary>Keeps the two largest (or smallest) values seen in <paramref name="top"/>; NaN = no sample yet, skipped like vanilla.</summary>
    internal static void Insert(Span<double> top, double value, bool largest)
    {
        if (double.IsNaN(value))
        {
            return;
        }
        for (int i = 0; i < top.Length; i++)
        {
            if (double.IsNaN(top[i]) || (largest ? value > top[i] : value < top[i]))
            {
                (top[i], value) = (value, top[i]);
                if (double.IsNaN(value))
                {
                    return;
                }
            }
        }
    }

    internal static double Sum(ReadOnlySpan<double> top)
    {
        double sum = 0;
        foreach (double v in top)
        {
            sum += double.IsNaN(v) ? 0 : v;
        }
        return sum;
    }

    /// <summary>Vanilla's float ceil(seconds x rate), clamped; saturates before the int conversion (infinity = max, never min).</summary>
    internal static int Ticks(double seconds, float ticksPerSecond, int min, int max)
    {
        float ticks = MathF.Ceiling((float)seconds * ticksPerSecond);
        return float.IsNaN(ticks) ? min : (int)Math.Clamp(ticks, min, max);
    }

    /// <summary>Raise at once; lower one tick per <c>hold</c> of continuous lower targets with fresh samples and a stable connection set.</summary>
    internal sealed class Hysteresis
    {
        private int _current = -1;
        private int _connections = -1;
        private long _lowerSince = -1;

        public int Next(int target, bool canLower, int connections, long now, long hold)
        {
            canLower &= connections == _connections;
            _connections = connections;
            if (_current < 0 || target >= _current || !canLower)
            {
                _current = Math.Max(_current, target);
                _lowerSince = -1;
            }
            else if (_lowerSince < 0)
            {
                _lowerSince = now;
            }
            else if (now - _lowerSince >= hold)
            {
                _current--;
                _lowerSince = now;
            }
            return _current;
        }
    }
}

/// <summary>
/// InternalRemoteConnection.ReceivePingReturn: pins the sample source <see cref="InputDelay"/> reads (last 20 round
/// trips in _returnedPings). If Halfling changes how samples are kept, the mod rolls back to vanilla.
/// </summary>
internal static class InputDelaySamples
{
    private const string IlSha256 = "6B7545A557BB25DB6789A7CC1472307AA0B7678634DBCC409C6624C77A3E06ED";

    internal static MethodInfo Target =>
        AccessTools.Method(typeof(NetworkMessenger.InternalRemoteConnection), "ReceivePingReturn")
        ?? throw new MissingMethodException("InternalRemoteConnection", "ReceivePingReturn");

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        Patcher.RequireIl(instructions, IlSha256, "InternalRemoteConnection.ReceivePingReturn");
}
