using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Performance;

/// <summary>
/// Replacement storage for one EventHandler&lt;EventArgs&gt; event with many short-lived subscribers: per owner, an ordered
/// multiset with O(1) add/remove, and one forwarding handler on the real event that calls a snapshot in subscription
/// order, as a multicast delegate does. Remove drops the last equal handler, like Delegate.Remove. Thread-safe like an
/// event's add/remove. shortcut: subscribers added straight to the real event after the forwarder run after all of these.
/// </summary>
internal sealed class OrderedEvent<TOwner> where TOwner : class
{    /// <summary>Ordered multiset of handlers; the owner's event holds only <see cref="Raise"/>.</summary>
    private sealed class Subscribers
    {
        internal readonly LinkedList<EventHandler<EventArgs>> Order = new();
        internal readonly Dictionary<EventHandler<EventArgs>, Stack<LinkedListNode<EventHandler<EventArgs>>>> Nodes = new();
        internal EventHandler<EventArgs>[] Snapshot = new EventHandler<EventArgs>[64];
        internal bool Attached;

        internal void Raise(object? sender, EventArgs e)
        {
            int n;
            EventHandler<EventArgs>[] snapshot;
            lock (this)
            {
                n = Order.Count;
                if (Snapshot.Length < n) Snapshot = new EventHandler<EventArgs>[Math.Max(n, Snapshot.Length * 2)];
                snapshot = Snapshot;
                Snapshot = []; // a nested raise allocates its own snapshot
                Order.CopyTo(snapshot, 0);
            }
            try
            {
                for (int i = 0; i < n; i++) snapshot[i](sender, e);
            }
            finally
            {
                Array.Clear(snapshot, 0, n);
                lock (this) if (snapshot.Length > Snapshot.Length) Snapshot = snapshot;
            }
        }
    }

    private readonly ConditionalWeakTable<TOwner, Subscribers> _owners = new();
    private readonly Action<TOwner, EventHandler<EventArgs>> _attach;

    internal OrderedEvent(Action<TOwner, EventHandler<EventArgs>> attach) => _attach = attach;

    /// <summary>owner.Event += handler (null is a no-op, as for an event); the forwarder is attached on first use.</summary>
    internal void Add(TOwner owner, EventHandler<EventArgs>? handler)
    {
        if (handler == null) return;
        Subscribers subs = _owners.GetValue(owner, _ => new Subscribers()); // factory may run twice; only the stored one is used
        lock (subs)
        {
            if (!subs.Attached)
            {
                subs.Attached = true;
                _attach(owner, subs.Raise);
            }
            LinkedListNode<EventHandler<EventArgs>> node = subs.Order.AddLast(handler);
            if (!subs.Nodes.TryGetValue(handler, out Stack<LinkedListNode<EventHandler<EventArgs>>>? stack)) subs.Nodes[handler] = stack = new();
            stack.Push(node);
        }
    }

    /// <summary>owner.Event -= handler: drops the last equal handler, if any (Delegate.Remove semantics).</summary>
    internal void Remove(TOwner owner, EventHandler<EventArgs>? handler)
    {
        if (handler == null || !_owners.TryGetValue(owner, out Subscribers? subs)) return;
        lock (subs)
        {
            if (!subs.Nodes.TryGetValue(handler, out Stack<LinkedListNode<EventHandler<EventArgs>>>? stack)) return;
            subs.Order.Remove(stack.Pop());
            if (stack.Count == 0) subs.Nodes.Remove(handler);
        }
    }
}

