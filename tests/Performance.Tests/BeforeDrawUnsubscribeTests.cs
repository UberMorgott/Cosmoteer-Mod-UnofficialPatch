using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Graphics;
using Cosmoteer.Ships.Rendering;
using Halfling.Graphics;
using Halfling.Geometry;
using Halfling.Scene2D;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

public class BeforeDrawUnsubscribeTests
{
    private const string HarmonyId = "performance.tests.before-draw-unsubscribe";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeCleanupAllocationsScaleLinearly(bool retainSurvivors)
    {
        using var patch = new Patches();
        _ = CleanupBytes(16, retainSurvivors);
        long small = CleanupBytes(512, retainSurvivors), large = CleanupBytes(2048, retainSurvivors);
        Assert.InRange(large, 0, 2048 * 200);
        Assert.True(large < small * 6 + 32768, $"cleanup allocation grew {small} -> {large}");
        TestContext.Current.TestOutputHelper!.WriteLine($"512 handlers: {small} bytes; 2048 handlers: {large} bytes");
    }

    private static long CleanupBytes(int count, bool retainSurvivors)
    {
        Publisher owner = Publisher.Create(count * 2 + 4);
        var expected = new List<EventHandler<DrawEventArgs>>();
        for (int i = 0; i < count; i++)
        {
            if (retainSurvivors && i % 4 == 0)
            {
                string label = "keep" + i;
                EventHandler<DrawEventArgs> persistent = (_, _) => owner.Trace.Add(label);
                expected.Add(persistent);
                owner.BeforeDraw += persistent;
            }
            var clean = new Clean(owner, i.ToString(), patched: true);
            owner.BeforeDraw += clean.Handler;
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        owner.Raise();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, Snapshot(owner)?.GetInvocationList() ?? []);
        return bytes;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(43)]
    public void MixedNativeMutationsDuplicatesAndMulticastMatchVanilla(int seed)
    {
        string expected = Mixed(seed, patched: false);
        using var patch = new Patches();
        Assert.Equal(expected, Mixed(seed, patched: true));
    }

    private static string Mixed(int seed, bool patched)
    {
        Publisher owner = Publisher.Create(256);
        var random = new Random(seed);
        var handlers = new EventHandler<DrawEventArgs>[16];
        var acted = new bool[handlers.Length];
        for (int i = 0; i < handlers.Length; i++)
        {
            int id = i;
            handlers[i] = (_, _) =>
            {
                owner.Trace.Add(id.ToString());
                if (id % 4 == 0)
                {
                    if (patched) BeforeDrawUnsubscribe.RemoveClean(owner, handlers[id]);
                    else owner.BeforeDraw -= handlers[id];
                }
                else if (!acted[id])
                {
                    acted[id] = true;
                    if (id % 4 == 1)
                    {
                        owner.BeforeDraw -= handlers[(id + 1) % handlers.Length];
                        owner.BeforeDraw += handlers[(id + 1) % handlers.Length];
                    }
                    else if (id % 4 == 2)
                    {
                        owner.BeforeDraw -= handlers[(id + 1) % handlers.Length] + handlers[(id + 2) % handlers.Length];
                    }
                }
            };
        }
        for (int i = 0; i < 48; i++) owner.BeforeDraw += handlers[random.Next(handlers.Length)];
        owner.Raise();
        owner.Raise();
        string remaining = string.Join(",", Snapshot(owner)?.GetInvocationList().Select(h => Array.FindIndex(handlers, original => original.Equals(h))) ?? []);
        return string.Join(",", owner.Trace) + "|" + remaining;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedSnapshotsMatchVanilla(bool sameOwner)
    {
        string expected = Nested(sameOwner, patched: false);
        using var patch = new Patches();
        Assert.Equal(expected, Nested(sameOwner, patched: true));
    }

    [Fact]
    public void NestedEmptySnapshotThenReaddedHandlerMatchesVanilla()
    {
        string expected = EmptyThenReadd(patched: false);
        using var patch = new Patches();
        Assert.Equal(expected, EmptyThenReadd(patched: true));
    }

    private static string EmptyThenReadd(bool patched)
    {
        Publisher owner = Publisher.Create();
        var first = new Clean(owner, "first", patched);
        var last = new Clean(owner, "last", patched);
        var added = new Clean(owner, "added", patched);
        EventHandler<DrawEventArgs> mutation = null!;
        mutation = (_, _) =>
        {
            owner.BeforeDraw -= first.Handler;
            owner.BeforeDraw -= mutation;
            owner.BeforeDraw -= last.Handler;
            Assert.Null(Snapshot(owner));
            owner.Raise(); // no BeforeDraw snapshot: must not disturb the outer invocation's active depth
            owner.BeforeDraw += added.Handler;
            owner.Raise(); // a new nested snapshot after the native list was completely emptied
            Assert.Null(Snapshot(owner));
        };
        owner.BeforeDraw += first.Handler;
        owner.BeforeDraw += mutation;
        owner.BeforeDraw += last.Handler;
        owner.Raise(); // the captured outer snapshot still calls last despite removal from the publisher
        Assert.Null(Snapshot(owner));
        return string.Join(",", owner.Trace);
    }

    private static string Nested(bool sameOwner, bool patched)
    {
        Publisher a = Publisher.Create(), b = sameOwner ? a : Publisher.Create();
        var first = new Clean(a, "a", patched);
        var last = new Clean(a, "z", patched);
        bool entered = false;
        EventHandler<DrawEventArgs> nested = (_, _) =>
        {
            a.Trace.Add("nested");
            if (entered) return;
            entered = true;
            b.Raise();
        };
        a.BeforeDraw += first.Handler;
        a.BeforeDraw += nested;
        a.BeforeDraw += last.Handler;
        if (!sameOwner)
        {
            b.BeforeDraw += (_, _) =>
            {
                b.Trace.Add("b");
                if (patched) BeforeDrawUnsubscribe.RemoveClean(a, last.Handler);
                else a.BeforeDraw -= last.Handler;
                a.BeforeDraw += first.Handler;
            };
        }
        a.Raise();
        a.Raise();
        return string.Join(",", a.Trace) + "|" + string.Join(",", b.Trace) + "|" + (Snapshot(a)?.GetInvocationList().Length ?? 0);
    }

    [Fact]
    public void ExceptionFlushesPartialRemovalsAndNextDrawStillWorks()
    {
        using var patch = new Patches();
        Publisher owner = Publisher.Create();
        var first = new Clean(owner, "first", true);
        var last = new Clean(owner, "last", true);
        var failure = new InvalidOperationException("handler failure");
        EventHandler<DrawEventArgs> throwing = (_, _) => throw failure;
        owner.BeforeDraw += first.Handler;
        owner.BeforeDraw += throwing;
        owner.BeforeDraw += last.Handler;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => owner.Raise()));
        Assert.Equal([throwing, last.Handler], Snapshot(owner)!.GetInvocationList());
        owner.BeforeDraw -= throwing;
        owner.Raise();
        Assert.Null(Snapshot(owner));
        Assert.Equal(["first", "last", "draw"], owner.Trace);
        var outside = new Clean(owner, "outside", true);
        owner.BeforeDraw += outside.Handler;
        BeforeDrawUnsubscribe.RemoveClean(owner, outside.Handler);
        Assert.Null(Snapshot(owner)); // a failed invocation must not leave an active context behind
    }

    [Fact]
    public void OtherSceneComponentsRetainImmediateNativeRemoval()
    {
        using var patch = new Patches();
        var owner = new DrawTemplate();
        EventHandler<DrawEventArgs> clean = null!;
        clean = (_, _) =>
        {
            BeforeDrawUnsubscribe.RemoveClean(owner, clean);
            Assert.Null(Snapshot(owner));
        };
        owner.BeforeDraw += clean;
        ((IRenderableSceneObject)owner).Draw(null!, null!, null!, default);
        Assert.Null(Snapshot(owner));
    }

    [Fact]
    public void OrdinaryMulticastAndNullMutationsFlushEarlierCleanRemovals()
    {
        using var patch = new Patches();
        Publisher owner = Publisher.Create();
        var first = new Clean(owner, "first", true);
        EventHandler<DrawEventArgs> x = (_, _) => owner.Trace.Add("x"), y = (_, _) => owner.Trace.Add("y");
        EventHandler<DrawEventArgs> mutation = (_, _) =>
        {
            owner.BeforeDraw += null;
            Assert.DoesNotContain(first.Handler, Snapshot(owner)!.GetInvocationList());
            BeforeDrawUnsubscribe.RemoveClean(owner, x + y); // multicast is deliberately a native mutation barrier
        };
        owner.BeforeDraw += first.Handler;
        owner.BeforeDraw += mutation;
        owner.BeforeDraw += x;
        owner.BeforeDraw += y;
        owner.Raise();
        Assert.Equal(["first", "x", "y", "draw"], owner.Trace);
        Assert.Equal(mutation, Snapshot(owner));
    }

    [Fact]
    public void RealPartGraphicsDirtyCleanAndReaddFlagsKeepNativeOrder()
    {
        using var patch = new Patches();
        Publisher owner = Publisher.Create();
        PartGraphics graphics = CreateGraphics(owner, flags: 3);
        EventHandler<DrawEventArgs> update = AccessTools.Method(typeof(PartGraphics), "UpdateColor").CreateDelegate<EventHandler<DrawEventArgs>>(graphics);
        owner.BeforeDraw += update;
        owner.Raise(); // dirty -> update quad colors, stay registered until the next draw
        Assert.Equal(2, Flags(graphics));
        Assert.Equal(update, Snapshot(owner));
        bool recolored = false;
        EventHandler<DrawEventArgs> recolor = (_, _) =>
        {
            if (recolored) return;
            recolored = true;
            AccessTools.Method(typeof(PartGraphics), "OnColorChanged").CreateDelegate<Action<object?, EventArgs>>(graphics)(null, EventArgs.Empty);
        };
        owner.BeforeDraw += recolor;
        owner.Raise(); // clean self-remove -> Registered cleared -> later recolor -> remove flushed before append
        Assert.Equal(3, Flags(graphics));
        Assert.Equal([recolor, update], Snapshot(owner)!.GetInvocationList());
        owner.BeforeDraw -= recolor;
        owner.Raise();
        Assert.Equal(2, Flags(graphics));
        owner.Raise();
        Assert.Equal(0, Flags(graphics));
        Assert.Null(Snapshot(owner));
    }

    [Fact]
    public void RealPartGraphicsDetachmentUsesOrdinaryRemoveBarrier()
    {
        using var patch = new Patches();
        Publisher owner = Publisher.Create();
        PartGraphics graphics = CreateGraphics(owner, flags: 2);
        var first = new Clean(owner, "first", true);
        EventHandler<DrawEventArgs> update = AccessTools.Method(typeof(PartGraphics), "UpdateColor").CreateDelegate<EventHandler<DrawEventArgs>>(graphics);
        EventHandler<DrawEventArgs> detach = (_, _) => AccessTools.Method(typeof(PartGraphics), "OnPartDetaching").CreateDelegate<Action>(graphics)();
        owner.BeforeDraw += first.Handler;
        owner.BeforeDraw += detach;
        owner.BeforeDraw += update;
        owner.Raise();
        Assert.Equal(0, Flags(graphics));
        Assert.Equal(detach, Snapshot(owner));
        Assert.Equal(["first", "draw"], owner.Trace);
    }

    private static PartGraphics CreateGraphics(Publisher renderer, int flags)
    {
        var ship = (Ship)RuntimeHelpers.GetUninitializedObject(typeof(Ship));
        AccessTools.Field(typeof(Ship), "<Renderer>k__BackingField").SetValue(ship, renderer);
        var part = (Part)RuntimeHelpers.GetUninitializedObject(typeof(Part));
        AccessTools.Field(typeof(CommonBasePart), "p_ship").SetValue(part, ship);
        var graphics = (PartGraphics)RuntimeHelpers.GetUninitializedObject(typeof(PartGraphics));
        AccessTools.Field(typeof(PartComponent), "<Part>k__BackingField").SetValue(graphics, part);
        FieldInfo status = AccessTools.Field(typeof(PartGraphics), "_colorUpdateStatus");
        status.SetValue(graphics, Enum.ToObject(status.FieldType, flags));
        AccessTools.Field(typeof(PartGraphics), "_getColorFrom").SetValue(graphics, new ColorSource());
        return graphics;
    }

    private static int Flags(PartGraphics graphics) => Convert.ToInt32(AccessTools.Field(typeof(PartGraphics), "_colorUpdateStatus").GetValue(graphics));

    private sealed class ColorSource : IPartComponentColor
    {
        public Color Color => Color.White;
        public event EventHandler<EventArgs> ColorChanged { add { } remove { } }
    }

    [Fact]
    public void CompletedCleanupDoesNotKeepPartHandlersAlive()
    {
        using var patch = new Patches();
        Publisher owner = Publisher.Create();
        WeakReference removed = CleanAndForget(owner);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(removed.IsAlive);
        GC.KeepAlive(owner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CleanAndForget(Publisher owner)
    {
        var handler = new Clean(owner, "clean", true);
        owner.BeforeDraw += handler.Handler;
        owner.Raise();
        return new WeakReference(handler);
    }

    [Fact]
    public void PinsRejectChangedBodiesAndAfterDrawRemainsNative()
    {
        foreach (MethodInfo target in BeforeDrawUnsubscribe.Targets)
        {
            var changed = PatchProcessor.GetOriginalInstructions(target);
            changed.Insert(0, new CodeInstruction(OpCodes.Nop));
            Assert.Throws<InvalidOperationException>(() => BeforeDrawUnsubscribe.Transpiler(changed, target).ToList());
        }
        MethodInfo draw = BeforeDrawUnsubscribe.Targets[1];
        var original = PatchProcessor.GetOriginalInstructions(draw);
        int after = original.FindIndex(c => c.operand is FieldInfo { Name: "E_AFTER_DRAW" });
        string[] suffix = original.Skip(after).Select(c => c.ToString()).ToArray();
        var patched = BeforeDrawUnsubscribe.Transpiler(original, draw).ToList();
        int patchedAfter = patched.FindIndex(c => c.operand is FieldInfo { Name: "E_AFTER_DRAW" });
        Assert.Equal(suffix, patched.Skip(patchedAfter).Select(c => c.ToString()));
        using var scope = new Patches();
        Publisher owner = Publisher.Create();
        owner.BeforeDraw += (_, _) => owner.Trace.Add("before");
        owner.AfterDraw += (_, _) => owner.Trace.Add("after");
        owner.Raise();
        Assert.Equal(["before", "draw", "after"], owner.Trace);
    }

    private static EventHandler<DrawEventArgs>? Snapshot(SceneComponent owner) => owner._events.Get<EventHandler<DrawEventArgs>>(SceneComponent.E_BEFORE_DRAW);

    private sealed class Patches : IDisposable
    {
        public Patches()
        {
            var harmony = new Harmony(HarmonyId);
            foreach (var target in BeforeDrawUnsubscribe.Targets)
                harmony.Patch(target, transpiler: new HarmonyMethod(typeof(BeforeDrawUnsubscribe), nameof(BeforeDrawUnsubscribe.Transpiler)));
        }
        public void Dispose() => new Harmony(HarmonyId).UnpatchAll(HarmonyId);
    }

    private sealed class Clean
    {
        private readonly Publisher _owner;
        private readonly string _name;
        private readonly bool _patched;
        public readonly EventHandler<DrawEventArgs> Handler;
        public Clean(Publisher owner, string name, bool patched)
        {
            _owner = owner;
            _name = name;
            _patched = patched;
            Handler = OnDraw;
        }
        private void OnDraw(object? sender, DrawEventArgs e)
        {
            _owner.Trace.Add(_name);
            if (_patched) BeforeDrawUnsubscribe.RemoveClean(_owner, Handler);
            else _owner.BeforeDraw -= Handler;
        }
    }

    private sealed class DrawTemplate : SceneComponent
    {
        public override void Draw(SceneViewport viewport, SceneCamera camera, SceneRoot root, RotRect viewRect) { }
    }

    private sealed class Publisher : ShipRenderer
    {
        private Publisher() : base(null!) { }
        public List<string> Trace = null!;
        public static Publisher Create(int capacity = 128)
        {
            var owner = (Publisher)RuntimeHelpers.GetUninitializedObject(typeof(Publisher));
            var field = AccessTools.Field(typeof(SceneComponent), "_drawFields");
            field.SetValue(owner, field.GetValue(new DrawTemplate()));
            owner.Trace = new List<string>(capacity);
            return owner;
        }
        public void Raise() => ((IRenderableSceneObject)this).Draw(null!, null!, null!, default);
        public override void Draw(SceneViewport viewport, SceneCamera camera, SceneRoot root, RotRect viewRect) => Trace.Add("draw");
    }
}
