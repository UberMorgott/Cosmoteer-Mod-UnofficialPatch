using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

public sealed class SoundStopHubTests
{
    [Fact]
    public void KillSwitchIsOnlyZero()
    {
        Assert.False(SoundStopHub.IsRequested("0"));
        Assert.True(SoundStopHub.IsRequested(null));
        Assert.True(SoundStopHub.IsRequested("1"));
        Assert.Equal(4, SoundStopHub.Patches.Length);
    }

    /// <summary>Against the real game IL: one receiver load becomes nop and one accessor call goes to the hub.</summary>
    [Fact]
    public void TranspilersMatchRealGameIl()
    {
        foreach ((var target, _, bool add) in SoundStopHub.Targets)
        {
            Assert.False(SharedGenericRetarget.IsShared(target));
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(target);
            List<CodeInstruction> code = SoundStopHub.Transpiler(PatchProcessor.GetOriginalInstructions(target), target).ToList();
            Assert.Equal(original.Count, code.Count);
            var changed = Enumerable.Range(0, code.Count).Where(i => code[i].opcode != original[i].opcode || code[i].operand?.ToString() != original[i].operand?.ToString()).ToList();
            Assert.Equal(2, changed.Count);
            Assert.Equal(OpCodes.Ldfld, original[changed[0]].opcode);
            Assert.Equal(OpCodes.Nop, code[changed[0]].opcode);
            Assert.Equal(add ? "add_Stopped" : "remove_Stopped", ((System.Reflection.MethodInfo)original[changed[1]].operand).Name);
            Assert.Equal(AccessTools.Method(typeof(SoundStopHub), add ? nameof(SoundStopHub.Add) : nameof(SoundStopHub.Remove)), code[changed[1]].operand);
        }
    }

    [Fact]
    public void TranspilerRejectsChangedIl()
    {
        var target = SoundStopHub.Targets[0].Target;
        List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(target);
        code.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => SoundStopHub.Transpiler(code, target).ToList());
    }

    private sealed class Source
    {
        public event EventHandler<EventArgs>? Stopped;
        public void Raise() => Stopped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One world: subscribers that log their id when raised and run a scripted add/remove inside the raise.</summary>
    private sealed class World
    {
        internal readonly List<int> Log = [];
        internal readonly Action<int>[] AddOf, RemoveOf;
        internal readonly Action<int, int> AddPair, RemovePair; // multicast handlers: flattened on add, contiguous run on remove
        internal readonly EventHandler<EventArgs>[] Handlers;
        internal Func<int, (int Kind, int Other)> Script = _ => (0, 0);

        internal World(int n, Action<EventHandler<EventArgs>> add, Action<EventHandler<EventArgs>> remove)
        {
            Handlers = new EventHandler<EventArgs>[n];
            for (int i = 0; i < n; i++)
            {
                int id = i;
                Handlers[i] = (_, _) =>
                {
                    Log.Add(id);
                    (int kind, int other) = Script(id);
                    if (kind == 1) remove(Handlers[other]); // removed during the raise: still runs this time
                    else if (kind == 2) add(Handlers[other]); // added during the raise: not run this time
                };
            }
            AddPair = (a, b) => add((EventHandler<EventArgs>)Delegate.Combine(Handlers[a], Handlers[b]));
            RemovePair = (a, b) => remove((EventHandler<EventArgs>)Delegate.Combine(Handlers[a], Handlers[b]));
            AddOf = Enumerable.Range(0, n).Select(i => (Action<int>)(_ => add(Handlers[i]))).ToArray();
            RemoveOf = Enumerable.Range(0, n).Select(i => (Action<int>)(_ => remove(Handlers[i]))).ToArray();
        }
    }

    /// <summary>
    /// Interleaved subscribe/unsubscribe (duplicates, unknown removes) and raises with adds/removes inside the raise: the hub
    /// calls handlers exactly as the plain multicast event does.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MatchesMulticastOrder(int seed)
    {
        const int n = 24;
        var plain = new Source();
        var hubbed = new Source();
        var hub = new OrderedEvent<Source>((s, forwarder) => s.Stopped += forwarder);
        var vanilla = new World(n, h => plain.Stopped += h, h => plain.Stopped -= h);
        var fast = new World(n, h => hub.Add(hubbed, h), h => hub.Remove(hubbed, h));
        var rng = new Random(seed);
        int raises = 0, calls = 0;
        for (int step = 0; step < 3000; step++)
        {
            int i = rng.Next(n);
            int j = rng.Next(n);
            switch (rng.Next(8))
            {
                case 0 or 1 or 2:
                    vanilla.AddOf[i](0);
                    fast.AddOf[i](0);
                    break;
                case 3 or 4:
                    vanilla.RemoveOf[i](0);
                    fast.RemoveOf[i](0);
                    break;
                case 5:
                    vanilla.AddPair(i, j);
                    fast.AddPair(i, j);
                    break;
                case 6:
                    vanilla.RemovePair(i, j);
                    fast.RemovePair(i, j);
                    break;
                default:
                    var script = Enumerable.Range(0, n).Select(_ => (rng.Next(10) switch { < 4 => 1, 4 => 2, _ => 0 }, rng.Next(n))).ToArray();
                    vanilla.Script = fast.Script = Lookup(script);
                    plain.Raise();
                    hubbed.Raise();
                    Assert.Equal(vanilla.Log, fast.Log);
                    calls += vanilla.Log.Count;
                    vanilla.Log.Clear();
                    fast.Log.Clear();
                    raises++;
                    break;
            }
        }
        Assert.True(raises > 300);
        Assert.True(calls > 1000);
    }

    private static Func<int, (int Kind, int Other)> Lookup((int, int)[] script) => id => script[id];

    /// <summary>The game's order: first virtual instance, the manager's removal handler, then the later virtual instances.</summary>
    [Fact]
    public void ForwarderSitsAtFirstSubscription()
    {
        var source = new Source();
        var hub = new OrderedEvent<Source>((s, forwarder) => s.Stopped += forwarder);
        var log = new List<string>();
        EventHandler<EventArgs> v1 = (_, _) => log.Add("v1"), v2 = (_, _) => log.Add("v2"), ext = (_, _) => log.Add("ext");
        hub.Add(source, v1);
        hub.Add(source, ext);
        hub.Add(source, v2);
        EventHandler<EventArgs> stopV1 = (_, _) => hub.Remove(source, v1);
        hub.Add(source, stopV1); // removes v1 during the raise: v1 already ran
        source.Raise();
        Assert.Equal(["v1", "ext", "v2"], log);
        log.Clear();
        source.Raise();
        Assert.Equal(["ext", "v2"], log);
    }
}
