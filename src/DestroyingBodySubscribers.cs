using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Ships.Parts.Colliders;
using Halfling.Scene2D.Physics;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Every collider part subscribes to its ship's ScenePhysicsWrapper.DestroyingBody while it has a collider and
/// unsubscribes when it stops being operational (each destroyed part in combat). A ship has thousands of colliders, so
/// every unsubscribe rebuilt the ship's multicast delegate (O(n) search plus a new n-entry array; about 6% of all
/// allocations in a 40-ship battle).
/// <para/>
/// The four BaseCollider add/remove sites now use an <see cref="OrderedEvent{TOwner}"/> per physics wrapper: one forwarder on
/// the real event calls the colliders in subscription order on a snapshot. The game's only other subscriber of a ship's
/// wrapper is ShipPhysics, which subscribes once in its constructor, before any collider can, and never leaves, so the
/// raise order (ShipPhysics, then colliders in order) and a collider unsubscribing itself during the raise are as before.
/// Off: <c>PERFORMANCE_DESTROYING_BODY_SUBSCRIBERS=0</c>.
/// </summary>
internal static class DestroyingBodySubscribers
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_DESTROYING_BODY_SUBSCRIBERS"));
    internal static bool IsRequested(string? value) => value != "0";

    /// <summary>Method, IL sha256, DestroyingBody add sites, remove sites.</summary>
    private static readonly (string Name, string Sha256, int Adds, int Removes)[] Methods =
    [
        ("OnOperational", "ED3F00C68057AE91160D36F5E1259456C936B00AC93900AE8C84BA4338CBB5BC", 1, 0),
        ("OnNonOperational", "1449DA3F0D47AC3573234A708A0366FB58F412137606402C69F8647656FE3E50", 0, 1),
        ("OnCreatedBody", "610240ED1CC9D4A5133DBE6BF2DB3944939DD50E44902A2C928347F4B4A32C3E", 1, 0),
        ("OnDestroyingBody", "0D8968FA0283B986B586965B21F56F14A2F6897D4C2B74E40C04E9A60F4BF3E5", 0, 1),
    ];

    internal static readonly MethodInfo[] Targets = Methods.Select(m => AccessTools.Method(typeof(BaseCollider), m.Name)
        ?? throw new MissingMethodException(nameof(BaseCollider), m.Name)).ToArray();

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? Targets.Select(t => ((MethodBase)t, typeof(DestroyingBodySubscribers))).ToArray() : [];

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        (string name, string sha256, int adds, int removes) = Methods.Single(m => m.Name == original.Name);
        List<CodeInstruction> code = Patcher.RequireIl(instructions, sha256, "BaseCollider." + name);
        MethodInfo add = AccessTools.Method(typeof(ScenePhysicsWrapper), "add_DestroyingBody");
        MethodInfo remove = AccessTools.Method(typeof(ScenePhysicsWrapper), "remove_DestroyingBody");
        int added = 0, removed = 0;
        foreach (CodeInstruction ci in code)
        {
            if (ci.Calls(add))
            {
                ci.opcode = OpCodes.Call;
                ci.operand = AccessTools.Method(typeof(DestroyingBodySubscribers), nameof(Add));
                added++;
            }
            else if (ci.Calls(remove))
            {
                ci.opcode = OpCodes.Call;
                ci.operand = AccessTools.Method(typeof(DestroyingBodySubscribers), nameof(Remove));
                removed++;
            }
        }
        if (added != adds || removed != removes) throw new InvalidOperationException($"BaseCollider.{name}: {added}/{removed} DestroyingBody add/remove calls, expected {adds}/{removes}");
        return code;
    }

    private static readonly OrderedEvent<ScenePhysicsWrapper> Destroying = new((wrapper, forwarder) => wrapper.DestroyingBody += forwarder);

    /// <summary>wrapper.DestroyingBody += handler.</summary>
    internal static void Add(ScenePhysicsWrapper wrapper, EventHandler<EventArgs>? handler) => Destroying.Add(wrapper, handler);

    /// <summary>wrapper.DestroyingBody -= handler.</summary>
    internal static void Remove(ScenePhysicsWrapper wrapper, EventHandler<EventArgs>? handler) => Destroying.Remove(wrapper, handler);
}
