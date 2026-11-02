using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships.Parts.Effects;
using Cosmoteer.Simulation.Cameras;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Every continuous part effect with a zoom limit (thrusters, glows...) subscribes to the camera's ZoomChanged event when
/// it becomes operational and unsubscribes when it stops. With thousands of such parts, every unsubscribe (each destroyed
/// or unpowered part in combat) rebuilt the camera's multicast delegate: an O(n) search and a new n-entry array, about
/// 5% of all allocations in a 40-ship battle.
/// <para/>
/// The two PartContinuousEffects calls now go to an ordered per-camera subscriber list with O(1) add and remove, and the
/// camera event gets one forwarding handler that calls the subscribers in subscription order on a snapshot, as a multicast
/// delegate does; remove takes the last equal handler like Delegate.Remove. Zoom handling is presentation only (media
/// effect refresh) and runs in the same order. shortcut: a foreign ZoomChanged subscriber added between effect subscriptions
/// is called after all of them (vanilla interleaves); the game itself has no other subscriber. Off: <c>PERFORMANCE_ZOOM_SUBSCRIBERS=0</c>.
/// </summary>
internal static class ZoomSubscribers
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_ZOOM_SUBSCRIBERS"));
    internal static bool IsRequested(string? value) => value != "0";

    private static readonly (string Name, string Sha256, string Accessor)[] Methods =
    [
        ("OnOperational", "7CC72F851F8FAB34965B9D4881E851355AD5FCCC93164CB7EDF5ACDBB03F84C9", "add_ZoomChanged"),
        ("OnNonOperational", "C47E4941423619B9A449A03A02DCEFCDA0F244D26260580A538269178A3736A6", "remove_ZoomChanged"),
    ];

    internal static readonly MethodInfo[] Targets = Methods.Select(m => AccessTools.Method(typeof(PartContinuousEffects), m.Name, Type.EmptyTypes)
        ?? throw new MissingMethodException(nameof(PartContinuousEffects), m.Name)).ToArray();

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? Targets.Select(t => ((MethodBase)t, typeof(ZoomSubscribers))).ToArray() : [];

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        (string name, string sha256, string accessor) = Methods.Single(m => m.Name == original.Name);
        List<CodeInstruction> code = Patcher.RequireIl(instructions, sha256, "PartContinuousEffects." + name);
        MethodInfo vanilla = AccessTools.Method(typeof(CameraNode), accessor);
        MethodInfo replacement = AccessTools.Method(typeof(ZoomSubscribers), accessor == "add_ZoomChanged" ? nameof(Add) : nameof(Remove));
        int sites = 0;
        foreach (CodeInstruction ci in code)
        {
            if (!ci.Calls(vanilla)) continue;
            ci.opcode = OpCodes.Call;
            ci.operand = replacement;
            sites++;
        }
        if (sites != 1) throw new InvalidOperationException($"PartContinuousEffects.{name}: {sites} {accessor} calls, expected 1");
        return code;
    }

    private static readonly OrderedEvent<CameraNode> Zoom = new((camera, forwarder) => camera.ZoomChanged += forwarder);

    /// <summary>camera.ZoomChanged += handler.</summary>
    internal static void Add(CameraNode camera, EventHandler<EventArgs>? handler) => Zoom.Add(camera, handler);

    /// <summary>camera.ZoomChanged -= handler.</summary>
    internal static void Remove(CameraNode camera, EventHandler<EventArgs>? handler) => Zoom.Remove(camera, handler);
}