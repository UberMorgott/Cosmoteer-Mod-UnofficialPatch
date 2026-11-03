using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Cosmoteer.Game.Multiplayer;
using Halfling.Network;
using Halfling.Timing;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>
/// MP freeze telemetry, log only (every peer, main thread): per remote player the margin in ticks with which each InputTick
/// arrived (0 = just in time, after the sim had to wait for it), sim stalls waiting for inputs (count, total, longest,
/// including one still open), and on the host the game's input delay and each connection's ping window. One summary line every 10 s; counters are preallocated, only the summary line allocates.
/// </summary>
internal static class NetTelemetry
{
    private static readonly long Interval = Stopwatch.Frequency * 10;
    private static readonly string[] BucketNames = ["0", "1", "2", "3", "4-5", "6+"];

    // Weak keys: a finished game's manager (and its sim) is not kept alive by its counters.
    private static readonly ConditionalWeakTable<BaseMPManager, State> States = new();

    internal static int Bucket(int slack) => slack switch
    {
        <= 0 => 0,
        <= 3 => slack,
        <= 5 => 4,
        _ => 5,
    };

    private sealed class State
    {
        public readonly Dictionary<MessengerID, int[]> SlackByPlayer = new();
        public readonly StallClock Stalls = new();
        public long LastSummary = Stopwatch.GetTimestamp();
    }

    internal static void OnArrival(BaseMPManager mp, BaseMPManager.InputTick it)
    {
        if (it.InstanceID != mp.InstanceID)
        {
            return;
        }
        Dictionary<MessengerID, int[]> slack = States.GetValue(mp, _ => new State()).SlackByPlayer;
        if (!slack.TryGetValue(it.PlayerID, out int[]? hist))
        {
            slack[it.PlayerID] = hist = new int[BucketNames.Length];
        }
        hist[Bucket(it.Tick - mp.NetworkInputTick - 1)]++; // tick cur+1 is the next one the sim needs
    }

    internal static void OnReadyCheck(BaseMPManager mp, bool ready)
    {
        State s = States.GetValue(mp, _ => new State());
        long now = Stopwatch.GetTimestamp();
        s.Stalls.Check(ready, now);
        if (now - s.LastSummary < Interval)
        {
            return;
        }
        s.Stalls.Account(now);
        Log.Info(Summary(mp, s, now - s.LastSummary));
        s.LastSummary = now;
        s.Stalls.ResetInterval();
        foreach (int[] hist in s.SlackByPlayer.Values)
        {
            Array.Clear(hist);
        }
    }

    /// <summary>
    /// Stall accounting per summary interval: a stall counts once (when it starts), its time is split across intervals
    /// by a cursor, and the longest is measured from its real start, so one 25 s stall is 1 stall with max 25 s.
    /// </summary>
    internal sealed class StallClock
    {
        private long _start = -1, _cursor;

        public int Count { get; private set; }
        public long Total { get; private set; }
        public long Max { get; private set; }

        public void Check(bool ready, long now)
        {
            if (!ready && _start < 0)
            {
                _start = _cursor = now;
                Count++;
            }
            else if (ready && _start >= 0)
            {
                Account(now);
                _start = -1;
            }
        }

        /// <summary>Adds the open stall's time up to <paramref name="now"/> to this interval.</summary>
        public void Account(long now)
        {
            if (_start < 0)
            {
                return;
            }
            Total += now - _cursor;
            _cursor = now;
            Max = Math.Max(Max, now - _start);
        }

        public void ResetInterval() => Count = (int)(Total = Max = 0);
    }
    private static string Summary(BaseMPManager mp, State s, long elapsed)
    {
        static long Ms(long t) => t * 1000 / Stopwatch.Frequency;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"net {Ms(elapsed)} ms: stalls {s.Stalls.Count} total {Ms(s.Stalls.Total)} ms max {Ms(s.Stalls.Max)} ms; slack ticks");
        foreach ((MessengerID player, int[] hist) in s.SlackByPlayer)
        {
            sb.Append(CultureInfo.InvariantCulture, $" [{player}");
            for (int i = 0; i < hist.Length; i++)
            {
                sb.Append(CultureInfo.InvariantCulture, $" {BucketNames[i]}:{hist[i]}");
            }
            sb.Append(']');
        }
        if (mp is MPHostManager host)
        {
            sb.Append(CultureInfo.InvariantCulture, $"; delay {host._inputTickDelay}");
            foreach (RemoteConnection c in host._connections)
            {
                double max = 0;
                if (c is NetworkMessenger.InternalRemoteConnection irc)
                {
                    foreach (Time ping in irc._returnedPings)
                    {
                        max = Math.Max(max, ping);
                    }
                }
                sb.Append(CultureInfo.InvariantCulture, $" [{c.TargetID} rtt mean {(double)c.Latency * 1000:0} max {max * 1000:0} ms loss {c.CurrentUnreliableMessageLossRate:0.00}]");
            }
        }
        return sb.ToString();
    }
}

/// <summary>BaseMPManager.OnInputTickReceived: arrival margin of every remote InputTick.</summary>
internal static class InputArrivalTelemetry
{
    internal static MethodInfo Target =>
        AccessTools.Method(typeof(BaseMPManager), "OnInputTickReceived")
        ?? throw new MissingMethodException(nameof(BaseMPManager), "OnInputTickReceived");

    public static void Prefix(BaseMPManager __instance, DeserializedMessageReceivedEventArgs<BaseMPManager.InputTick> e) =>
        NetTelemetry.OnArrival(__instance, e.Message.Object);
}

/// <summary>BaseMPManager.IsReadyForTick: stall start/end (false while any player's input is missing) and the periodic summary.</summary>
internal static class StallTelemetry
{
    internal static MethodInfo Target =>
        AccessTools.Method(typeof(BaseMPManager), "IsReadyForTick")
        ?? throw new MissingMethodException(nameof(BaseMPManager), "IsReadyForTick");

    public static void Postfix(BaseMPManager __instance, bool __result) => NetTelemetry.OnReadyCheck(__instance, __result);
}
