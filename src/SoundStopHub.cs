using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Simulation.MediaEffects;
using Halfling.Audio;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>
/// A looping sound shared by many effects (SharedSoundEffect) plays one real sound instance; each effect gets a virtual
/// instance that subscribes to the real instance's Stopped event and unsubscribes when it stops. With many virtual
/// instances per sound, every unsubscribe rebuilt the multicast delegate (a new n-entry array), about 26 MB of garbage
/// in a 40-ship battle.
/// <para/>
/// All four subscribe/unsubscribe sites of the real instance's Stopped (VirtualSoundInstance.Init / StopImpl and the
/// SharedSoundEffect.Stopped accessors, which forward to it) go to one <see cref="OrderedEvent{TOwner}"/> per
/// SharedSoundEffect: an ordered list with O(1) add/remove, and one forwarder on the real event (attached at the first
/// subscription, so it sits where the first handler did) that calls a snapshot in subscription order, exactly as the
/// multicast did (handlers removed during the raise still run). The real instance is private to its SharedSoundEffect,
/// so nothing else subscribes to it while the effect is alive. Audio is presentation only.
/// Off: <c>UNOFFICIAL_PATCH_SOUND_STOP_HUB=0</c>.
/// </summary>
internal static class SoundStopHub
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("UNOFFICIAL_PATCH_SOUND_STOP_HUB"));
    internal static bool IsRequested(string? value) => value != "0";

    private static readonly FieldInfo ActualInstance = AccessTools.Field(typeof(SharedSoundEffect), "_actualInstance")
        ?? throw new MissingFieldException(nameof(SharedSoundEffect), "_actualInstance");

    private static readonly MethodInfo NativeAdd = AccessTools.Method(typeof(SoundInstance), "add_Stopped");
    private static readonly MethodInfo NativeRemove = AccessTools.Method(typeof(SoundInstance), "remove_Stopped");

    internal static readonly (MethodInfo Target, string IlSha256, bool Add)[] Targets =
    [
        (AccessTools.Method(typeof(SharedSoundEffect.VirtualSoundInstance), "Init"), "CBF050D27E50FDBFB5934E45C9BE4F6ADF72DC82B24B2BC47F6623479ED1FB21", true),
        (AccessTools.Method(typeof(SharedSoundEffect.VirtualSoundInstance), "StopImpl"), "0D84E1517AA7577A00B1DAF794D70036D8E1B07006D32F91F72B9DD60DB69D0D", false),
        (AccessTools.Method(typeof(SharedSoundEffect), "add_Stopped"), "E2B784E56A50A0F53EDFBEDA33137B1DB9546B6782F4D4B3982854A89B003EE8", true),
        (AccessTools.Method(typeof(SharedSoundEffect), "remove_Stopped"), "04307A90023494AA8F8CEDF9948FA1B85B9F54E59CC4E00375017D14DBF3CF62", false),
    ];

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? Targets.Select(t => ((MethodBase)t.Target, typeof(SoundStopHub))).ToArray() : [];

    /// <summary><c>sse._actualInstance.Stopped += / -= handler</c> becomes <c>SoundStopHub.Add / Remove(sse, handler)</c>.</summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        (MethodInfo target, string sha, bool add) = Targets.Single(t => t.Target == original);
        string name = target.DeclaringType!.Name + "." + target.Name;
        List<CodeInstruction> code = Patcher.RequireIl(instructions, sha, name);
        MethodInfo native = add ? NativeAdd : NativeRemove;
        int call = code.FindIndex(ci => ci.Calls(native));
        if (call < 0 || code.FindLastIndex(ci => ci.Calls(native)) != call) throw new InvalidOperationException(name + ": expected one Stopped accessor call");
        // The receiver load: pinned IL puts it right before the handler (ldarg.1, or ldarg.0 + ldftn + newobj).
        int receiver = code.FindLastIndex(call, ci => ci.LoadsField(ActualInstance));
        if (receiver < 0 || call - receiver is not (2 or 4)) throw new InvalidOperationException(name + ": Stopped receiver not found");
        code[receiver].opcode = OpCodes.Nop; // leaves the SharedSoundEffect on the stack; labels kept
        code[receiver].operand = null;
        code[call].opcode = OpCodes.Call;
        code[call].operand = AccessTools.Method(typeof(SoundStopHub), add ? nameof(Add) : nameof(Remove));
        return code;
    }

    private static readonly OrderedEvent<SharedSoundEffect> Stopped = new((sse, forwarder) => sse._actualInstance.Stopped += forwarder);

    /// <summary><c>sse._actualInstance.Stopped += handler</c>.</summary>
    internal static void Add(SharedSoundEffect sse, EventHandler<EventArgs>? handler) => Stopped.Add(sse, handler);

    /// <summary><c>sse._actualInstance.Stopped -= handler</c>.</summary>
    internal static void Remove(SharedSoundEffect sse, EventHandler<EventArgs>? handler) => Stopped.Remove(sse, handler);
}
