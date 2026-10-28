using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cosmoteer.Game;
using Cosmoteer.Simulation;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Vanilla desync: PlayerState.AddMoney (<c>Money = clamp(Money + amount)</c>, unlocked; Money is in the integrity hash)
/// is called from parallel per-ship buckets: crew trade deliveries on their home ship (ShipCrewManager, bucket -13) and
/// pre-purchase refunds (ResourceManager, bucket -17). Two ships of one player on different threads in the same tick can
/// lose an update, so money differs between clients. During a parallel update the prefix only adds the delta to a
/// per-sim pending sum (Money stays constant, so parallel readers are deterministic too); SimRoot.ExecuteQueued, which
/// runs serially after every bucket, applies each player's exact sum once, in player Index order, before vanilla's queue.
/// The sum is order-free; saturation applies once to the bucket's total (vanilla's per-call order was racy anyway).
/// Serial calls run vanilla unchanged. Changes sim results versus vanilla: every MP player must run this mod.
/// </summary>
internal static class PlayerMoney
{
    private const string IlSha256 = "3E2EC7627B055E092A2D8E8F023786ECEABAC3AA7AA09B4BFF6DB59D69FAFC77";

    // Weak keys: a finished game's sim and its pending sums are collected together.
    private static readonly ConditionalWeakTable<SimRoot, Pending> PendingBySim = new();

    internal static MethodInfo Target =>
        AccessTools.Method(typeof(PlayerState), nameof(PlayerState.AddMoney))
        ?? throw new MissingMethodException(nameof(PlayerState), nameof(PlayerState.AddMoney));

    /// <summary>Deferral above assumes exactly vanilla's clamp-add-notify body; any change rolls the mod back to vanilla.</summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        Patcher.RequireIl(instructions, IlSha256, "PlayerState.AddMoney");

    /// <summary>Parallel update: defer the delta and skip vanilla. Every vanilla caller discards the return value.</summary>
    public static bool Prefix(PlayerState __instance, int amount, ref int __result)
    {
        if (__instance.Game.Sim is not { IsDoingParallelUpdate: true } sim)
        {
            return true;
        }
        PendingBySim.GetValue(sim, _ => new Pending()).Add(__instance, amount);
        __result = __instance.Money;
        return false;
    }

    /// <summary>Applies the sim's pending sums; serial only (ExecuteQueued), so AddMoney(0) runs vanilla to raise its event.</summary>
    internal static void Flush(SimRoot sim)
    {
        if (!PendingBySim.TryGetValue(sim, out Pending? pending))
        {
            return;
        }
        foreach ((PlayerState state, long sum) in pending.Drain())
        {
            state.Money = Apply(state.Money, sum);
            state.AddMoney(0);
        }
    }

    internal static int Apply(int money, long sum) => (int)Math.Clamp(money + sum, int.MinValue, int.MaxValue);

    /// <summary>Per-sim pending deltas; Add runs on FastParallel workers, Drain serially.</summary>
    internal sealed class Pending
    {
        private readonly Dictionary<PlayerState, long> _sums = new();

        public void Add(PlayerState state, int amount)
        {
            lock (_sums)
            {
                _sums.TryGetValue(state, out long sum);
                _sums[state] = sum + amount;
            }
        }

        /// <summary>Every pending (player, sum) in player Index order, then empty.</summary>
        public List<(PlayerState State, long Sum)> Drain()
        {
            lock (_sums)
            {
                List<(PlayerState, long)> result = _sums.OrderBy(p => p.Key.Index).Select(p => (p.Key, p.Value)).ToList();
                _sums.Clear();
                return result;
            }
        }
    }
}

/// <summary>SimRoot.ExecuteQueued: apply <see cref="PlayerMoney"/>'s pending sums before vanilla runs (and integrity-hashes after) the queue.</summary>
internal static class PlayerMoneyFlush
{
    private const string IlSha256 = "D48B112FE569BD01729A7619A6251FD91193E650F9D73FBA351A3903A2F883F1";

    internal static MethodInfo Target =>
        AccessTools.Method(typeof(SimRoot), nameof(SimRoot.ExecuteQueued))
        ?? throw new MissingMethodException(nameof(SimRoot), nameof(SimRoot.ExecuteQueued));

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        Patcher.RequireIl(instructions, IlSha256, "SimRoot.ExecuteQueued");

    public static void Prefix(SimRoot __instance, bool deterministic)
    {
        if (deterministic)
        {
            PlayerMoney.Flush(__instance);
        }
    }
}
