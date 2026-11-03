using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cosmoteer.Crew;
using Cosmoteer.Data;
using Cosmoteer.Modes.Career;
using Cosmoteer.Resources;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Crew.Jobs;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>
/// Vanilla desync: crew trade pickups/deliveries (ResourceTransferJob, ShipCrewManager bucket -13) call
/// Sim.Mode.OnResourceTradePickup/Delivery directly. The career override updates the trading station's offered
/// DeliverResourcesObjective Count/TargetCount (clamped, saved, in the integrity hash), so two ships trading with one
/// station in the same tick on different threads lose or reorder updates. During a parallel update the call is deferred
/// with vanilla's own convention for the same event (EventHub: EnqueueDeterministic keyed by crew.Form's ID, i.e. the
/// crew's ship), so it runs serially after the bucket in ID order; serial calls run vanilla unchanged.
/// Changes sim results versus vanilla: every MP player must run this mod.
/// </summary>
internal static class MissionTrade
{
    internal static readonly (MethodInfo Target, string IlSha256)[] Targets =
    [
        (Method(nameof(CareerSimModeManager.OnResourceTradePickup)), "134AC7316DAB2D8B30669DFAAF8D56342FFE63622BDCF5541F3B428D9518670B"),
        (Method(nameof(CareerSimModeManager.OnResourceTradeDelivery)), "49EB77F226519A8F42ABF7BF7E6E90377A107A6DA9793A7739A2B9307D9D1885"),
    ];

    private static MethodInfo Method(string name) =>
        AccessTools.Method(typeof(CareerSimModeManager), name) ?? throw new MissingMethodException(nameof(CareerSimModeManager), name);

    /// <summary>Deferral assumes vanilla's notify-objectives body; any change rolls the mod back to vanilla.</summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        (MethodInfo target, string sha) = Targets.Single(t => t.Target.MetadataToken == original.MetadataToken && t.Target.Module == original.Module);
        return Patcher.RequireIl(instructions, sha, $"{nameof(CareerSimModeManager)}.{target.Name}");
    }

    /// <summary>Parallel update: queue the same call (args captured now) and skip vanilla; the replay runs serially.</summary>
    public static bool Prefix(CareerSimModeManager __instance, MethodBase __originalMethod, TradeType tradeType,
        ID<ResourceRules> resourceType, int amount, CrewSoul crew, Ship? sourceShip, Ship? sinkShip)
    {
        if (__instance.Sim is not { IsDoingParallelUpdate: true } sim)
        {
            return true;
        }
        Action replay = __originalMethod.Name == nameof(CareerSimModeManager.OnResourceTradePickup)
            ? () => __instance.OnResourceTradePickup(tradeType, resourceType, amount, crew, sourceShip, sinkShip)
            : () => __instance.OnResourceTradeDelivery(tradeType, resourceType, amount, crew, sourceShip, sinkShip);
        sim.EnqueueDeterministic(crew.Form!.EnqueueDeterministicID, replay);
        return false;
    }
}
