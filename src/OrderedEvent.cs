using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace UnofficialPatch;

/// <summary>
/// Replacement storage for one EventHandler&lt;EventArgs&gt; event with many short-lived subscribers: per owner, an ordered
/// multiset with O(1) add/remove, and one forwarding handler on the real event that calls a snapshot in subscription
/// order, as a multicast delegate does. Remove drops the last equal handler, like Delegate.Remove. Thread-safe like an
/// event's add/remove. shortcut: subscribers added straight to the real event after the forwarder run after all of these.
/// </summary>
internal sealed class OrderedEvent<TOwner> where TOwner : class
{    /// <summary>Ordered multiset of handlers; the owner's event holds only <see cref="Raise"/>.</summary>
    private sealed class Subscribers(OrderedEvent<TOwner> owner)
    {
        internal readonly LinkedList<EventHandler<EventArgs>> Order = new();
        internal readonly Dictionary<EventHandler<EventArgs>, List<LinkedListNode<EventHandler<EventArgs>>>> Nodes = new();
        internal bool Attached;

        internal void Raise(object? sender, EventArgs e)
        {
            int n;
            EventHandler<EventArgs>[] snapshot;
            lock (this)
            {
                n = Order.Count;
                // One spare snapshot per event, shared by all owners; a nested or concurrent raise allocates its own.
                snapshot = Interlocked.Exchange(ref owner._spare, null) is { } spare && spare.Length >= n ? spare : new EventHandler<EventArgs>[Math.Max(n, 16)];
                Order.CopyTo(snapshot, 0);
            }
            try
            {
                for (int i = 0; i < n; i++) snapshot[i](sender, e);
            }
            finally
            {
                Array.Clear(snapshot, 0, n);
                Volatile.Write(ref owner._spare, snapshot);
            }
        }
    }

    private EventHandler<EventArgs>[]? _spare;

    private readonly ConditionalWeakTable<TOwner, Subscribers> _owners = new();
    private readonly Action<TOwner, EventHandler<EventArgs>> _attach;

    internal OrderedEvent(Action<TOwner, EventHandler<EventArgs>> attach) => _attach = attach;

    /// <summary>owner.Event += handler (null is a no-op, as for an event); the forwarder is attached on first use.</summary>
    internal void Add(TOwner owner, EventHandler<EventArgs>? handler)
    {
        if (handler == null) return;
        Subscribers subs = _owners.GetValue(owner, _ => new Subscribers(this)); // factory may run twice; only the stored one is used
        lock (subs)
        {
            if (!subs.Attached)
            {
                subs.Attached = true;
                _attach(owner, subs.Raise);
            }
            if (handler.HasSingleTarget) AddLast(subs, handler);
            else foreach (Delegate single in handler.GetInvocationList()) AddLast(subs, (EventHandler<EventArgs>)single); // as Delegate.Combine flattens
        }
    }

    private static void AddLast(Subscribers subs, EventHandler<EventArgs> handler)
    {
        LinkedListNode<EventHandler<EventArgs>> node = subs.Order.AddLast(handler);
        if (!subs.Nodes.TryGetValue(handler, out List<LinkedListNode<EventHandler<EventArgs>>>? nodes)) subs.Nodes[handler] = nodes = [];
        nodes.Add(node);
    }

    private static void Unlink(Subscribers subs, LinkedListNode<EventHandler<EventArgs>> node)
    {
        List<LinkedListNode<EventHandler<EventArgs>>> nodes = subs.Nodes[node.Value];
        nodes.RemoveAt(nodes.LastIndexOf(node));
        if (nodes.Count == 0) subs.Nodes.Remove(node.Value);
        subs.Order.Remove(node);
    }

    /// <summary>
    /// owner.Event -= handler with Delegate.Remove semantics: drops the last equal handler, or for a multicast handler the
    /// last contiguous run equal to its invocation list; nothing when there is none.
    /// </summary>
    internal void Remove(TOwner owner, EventHandler<EventArgs>? handler)
    {
        if (handler == null || !_owners.TryGetValue(owner, out Subscribers? subs)) return;
        lock (subs)
        {
            if (handler.HasSingleTarget)
            {
                if (subs.Nodes.TryGetValue(handler, out List<LinkedListNode<EventHandler<EventArgs>>>? nodes)) Unlink(subs, nodes[^1]);
                return;
            }
            Delegate[] run = handler.GetInvocationList(); // rare path: scan for the last matching run
            for (LinkedListNode<EventHandler<EventArgs>>? end = subs.Order.Last; end != null; end = end.Previous)
            {
                LinkedListNode<EventHandler<EventArgs>>? node = end;
                int i = run.Length - 1;
                while (i >= 0 && node != null && node.Value.Equals(run[i])) { node = node.Previous; i--; }
                if (i >= 0) continue;
                node = end;
                for (int k = 0; k < run.Length; k++) { LinkedListNode<EventHandler<EventArgs>>? prev = node!.Previous; Unlink(subs, node); node = prev; }
                return;
            }
        }
    }
}

