using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Xunit;

namespace UnofficialPatch.Tests;

public sealed class DestroyingBodySubscribersTests
{
    [Fact]
    public void KillSwitchIsOnlyZero()
    {
        Assert.False(DestroyingBodySubscribers.IsRequested("0"));
        Assert.True(DestroyingBodySubscribers.IsRequested(null));
    }

    [Fact]
    public void TranspilersMatchRealGameIl()
    {
        foreach (var target in DestroyingBodySubscribers.Targets)
            Assert.NotEmpty(DestroyingBodySubscribers.Transpiler(PatchProcessor.GetOriginalInstructions(target), target).ToList());
    }

    private sealed class Owner
    {
        public event EventHandler<EventArgs>? Evt;
        public void Raise() => Evt?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A first real subscriber (ShipPhysics) then many that unsubscribe themselves during the raise (colliders).</summary>
    [Fact]
    public void SelfRemovalDuringRaiseMatchesMulticast()
    {
        string Run(bool ordered)
        {
            var owner = new Owner();
            var log = new List<int>();
            var ev = new OrderedEvent<Owner>((o, f) => o.Evt += f);
            owner.Evt += (_, _) => log.Add(-1);
            var handlers = new EventHandler<EventArgs>[20];
            for (int i = 0; i < handlers.Length; i++)
            {
                int id = i;
                handlers[i] = (_, _) =>
                {
                    log.Add(id);
                    if (ordered) ev.Remove(owner, handlers[id]);
                    else owner.Evt -= handlers[id];
                };
                if (ordered) ev.Add(owner, handlers[i]);
                else owner.Evt += handlers[i];
            }
            owner.Raise();
            owner.Raise();
            for (int i = 0; i < handlers.Length; i += 3)
            {
                if (ordered) ev.Add(owner, handlers[i]);
                else owner.Evt += handlers[i];
            }
            owner.Raise();
            return string.Join(",", log);
        }
        Assert.Equal(Run(false), Run(true));
    }
}