using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships.Parts.Graphics;
using Cosmoteer.Ships.Rendering;
using Halfling.Performance;
using Halfling.Scene2D;
using HarmonyLib;

namespace Performance;

/// <summary>Batches only clean PartGraphics self-removals between native event mutation barriers.</summary>
internal static class BeforeDrawUnsubscribe
{
    private static readonly string[] Pins =
    [
        "BF9FBEEFAF23D1DB434B098A0131596BEE5C64D1C07D8DE6A2BC919B8B10D720",
        "31EABE0B69E457CD5BC1608C67ABE7081E3CC5494B96D60EB386B6D8972051F6",
        "5126767B303E4C099DDD7D0E9FD9D3D5A0CD60B7B37DD031C82F91AD7B9CF0B7",
        "FF119CCB2A29708F4A0EF1C880BE1929C2231C9C27BE46268381AB9BE1B1389A",
    ];
    private const string DependenciesPin = "545FD3BEDC15690A747186253ABB132A2043A47D7AFA7408D7F2C6A7A682D2EF";
    private static readonly ConditionalWeakTable<ShipRenderer, State> States = new();

    private sealed class HandlerComparer : IEqualityComparer<EventHandler<DrawEventArgs>>
    {
        public bool Equals(EventHandler<DrawEventArgs>? x, EventHandler<DrawEventArgs>? y) => x == y;
        public int GetHashCode(EventHandler<DrawEventArgs> handler) => HashCode.Combine(handler.GetHashCode(),
            handler.Target == null ? 0 : RuntimeHelpers.GetHashCode(handler.Target));
    }

    private sealed class State
    {
        public int ActiveDepth;
        public readonly Dictionary<EventHandler<DrawEventArgs>, int> Pending = new(new HandlerComparer());
    }

    internal static MethodInfo[] Targets =>
    [
        AccessTools.Method(typeof(PartGraphics), "UpdateColor"),
        AccessTools.Method(typeof(SceneComponent), "Halfling.Scene2D.IRenderableSceneObject.Draw"),
        AccessTools.EventAdder(typeof(SceneComponent), nameof(SceneComponent.BeforeDraw)),
        AccessTools.EventRemover(typeof(SceneComponent), nameof(SceneComponent.BeforeDraw)),
    ];

    internal static IEnumerable<MethodInfo> Dependencies()
    {
        yield return AccessTools.Method(typeof(SparseEventManager), nameof(SparseEventManager.Get)).MakeGenericMethod(typeof(EventHandler<DrawEventArgs>));
        yield return AccessTools.Method(typeof(SparseEventManager), nameof(SparseEventManager.Remove)).MakeGenericMethod(typeof(EventHandler<DrawEventArgs>));
        foreach (MethodInfo accessor in Targets.Skip(2))
            foreach (CodeInstruction code in PatchProcessor.GetOriginalInstructions(accessor))
                if (code.opcode == OpCodes.Ldftn) yield return (MethodInfo)code.operand;
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        int index = Array.FindIndex(Targets, t => t.Module == original.Module && t.MetadataToken == original.MetadataToken);
        if (index < 0) throw new MissingMethodException("Unsupported BeforeDraw patch target");
        List<CodeInstruction> code = Patcher.RequireIl(instructions, Pins[index], original.Name);
        Patcher.RequireIl(Dependencies().SelectMany(m => PatchProcessor.GetOriginalInstructions(m)), DependenciesPin, "BeforeDraw event dependencies");
        if (index == 0)
        {
            CodeInstruction remove = code.Single(c => c.Calls(Targets[3]));
            remove.opcode = OpCodes.Call;
            remove.operand = AccessTools.Method(typeof(BeforeDrawUnsubscribe), nameof(RemoveClean));
        }
        else if (index == 1)
        {
            int key = code.FindIndex(c => c.operand is FieldInfo { Name: "E_BEFORE_DRAW" });
            int invoke = code.FindIndex(key + 1, c => c.operand is MethodInfo { Name: "Invoke" } m && m.DeclaringType == typeof(EventHandler<DrawEventArgs>));
            code[invoke].opcode = OpCodes.Call;
            code[invoke].operand = AccessTools.Method(typeof(BeforeDrawUnsubscribe), nameof(Invoke));
            // Replace only this BeforeDraw Get receiver/key/call chain. The AfterDraw event remains native.
            int start = key - 2;
            var owner = new CodeInstruction(OpCodes.Ldarg_0);
            owner.labels.AddRange(code[start].labels);
            if (code.Skip(start + 1).Take(3).Any(c => c.labels.Count != 0) || code.Skip(start).Take(4).Any(c => c.blocks.Count != 0))
                throw new InvalidOperationException("Unsupported BeforeDraw Get boundaries");
            code.RemoveRange(start, 4);
            code.InsertRange(start, [owner, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BeforeDrawUnsubscribe), nameof(Get)))]);
        }
        else
        {
            // Every ordinary event mutation observes all earlier clean removals before native Combine/Remove runs.
            var owner = new CodeInstruction(OpCodes.Ldarg_0);
            owner.labels.AddRange(code[0].labels);
            code[0].labels.Clear();
            code.InsertRange(0, [owner, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BeforeDrawUnsubscribe), nameof(Flush)))]);
        }
        return code;
    }

    public static void RemoveClean(SceneComponent owner, EventHandler<DrawEventArgs>? handler)
    {
        // The pinned game call constructs one UpdateColor handler. Other callers/multicast values use native behavior.
        if (handler is { HasSingleTarget: true } && owner is ShipRenderer renderer && States.TryGetValue(renderer, out State? state)
            && state.ActiveDepth > 0)
        {
            state.Pending[handler] = state.Pending.GetValueOrDefault(handler) + 1;
        }
        else owner.BeforeDraw -= handler;
    }

    public static EventHandler<DrawEventArgs>? Get(SceneComponent owner)
    {
        Flush(owner);
        return owner._events.Get<EventHandler<DrawEventArgs>>(SceneComponent.E_BEFORE_DRAW);
    }

    public static void Invoke(EventHandler<DrawEventArgs> snapshot, object? sender, DrawEventArgs args)
    {
        if (sender is not ShipRenderer renderer)
        {
            snapshot(sender, args);
            return;
        }
        State state = States.GetValue(renderer, static _ => new State());
        state.ActiveDepth++;
        try { snapshot(sender, args); }
        finally
        {
            try { Flush(renderer); }
            finally
            {
                state.ActiveDepth--;
                if (state.ActiveDepth == 0) state.Pending.Clear();
            }
        }
    }

    public static void Flush(SceneComponent owner)
    {
        if (owner is not ShipRenderer renderer || !States.TryGetValue(renderer, out State? state) || state.Pending.Count == 0) return;
        try
        {
            EventHandler<DrawEventArgs>? current = owner._events.Get<EventHandler<DrawEventArgs>>(SceneComponent.E_BEFORE_DRAW);
            if (current == null) return;
            Delegate?[] handlers = current.GetInvocationList(); // one linear array instead of a descending array per removal
            int removed = 0;
            for (int i = handlers.Length - 1; i >= 0; i--)
            {
                var handler = (EventHandler<DrawEventArgs>)handlers[i]!;
                if (state.Pending.TryGetValue(handler, out int count) && count > 0)
                {
                    state.Pending[handler] = count - 1;
                    handlers[i] = null;
                    removed++;
                }
            }
            if (removed == 0) return;
            int kept = 0;
            foreach (Delegate? handler in handlers)
                if (handler != null) handlers[kept++] = handler;
            if (kept == 0)
                owner._events.Remove(SceneComponent.E_BEFORE_DRAW, current, static (_, _) => null);
            else
            {
                var replacement = (EventHandler<DrawEventArgs>)Delegate.Combine(handlers.AsSpan(0, kept))!;
                owner._events.Remove(SceneComponent.E_BEFORE_DRAW, replacement, static (_, handler) => handler);
            }
        }
        finally { state.Pending.Clear(); } // no removed handler/part references survive a completed or failed flush
    }
}
