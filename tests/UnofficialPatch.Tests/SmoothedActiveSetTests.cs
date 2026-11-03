using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Logic;
using HarmonyLib;
using Xunit;

namespace UnofficialPatch.Tests;

public sealed class SmoothedActiveSetTests : IDisposable
{
    private const string StubId = "performance.tests.smoothed-active-set";
    private const float MaxDelta = 12f; // settles within a few frames, so sleeping is exercised
    private static int _calls;
    private static Action<PartSmoothedValue>? _onUpdate;
    private readonly Harmony _harmony = new(StubId);

    /// <summary>Counts native UpdateValue calls and installs the mod's removal and setter hooks (Update itself is driven by Step).</summary>
    public SmoothedActiveSetTests()
    {
        _harmony.Patch(AccessTools.Method(typeof(PartSmoothedValue), "UpdateValue"),
            prefix: new HarmonyMethod(typeof(SmoothedActiveSetTests), nameof(CountUpdate)),
            postfix: new HarmonyMethod(typeof(SmoothedActiveSetTests), nameof(AfterUpdate)));
        foreach ((MethodBase target, Type patch) in SmoothedActiveSet.Targets.Where(t => t.Target != SmoothedActiveSet.Target))
            _harmony.Patch(target, finalizer: new HarmonyMethod(patch, "Finalizer"));
    }

    public void Dispose()
    {
        _harmony.UnpatchAll(StubId);
        _onUpdate = null;
    }

    private static void CountUpdate() => _calls++;

    private static void AfterUpdate(PartSmoothedValue __instance) => _onUpdate?.Invoke(__instance);

    private static void SetValue(object component, float value) => AccessTools.PropertySetter(component.GetType(), "Value").Invoke(component, [value]);

    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static readonly FieldInfo RulesField = AccessTools.Field(typeof(PartComponent), "<Rules>k__BackingField");

    private static void Remove(PartSmoothedValue.SmoothedValueManager manager, PartSmoothedValue value) =>
        SmoothedActiveSet.RemoveTarget.Invoke(manager, [value]);

    private static object? Handlers(object component) => AccessTools.Field(component.GetType(), "ValueChanged").GetValue(component);

    [Fact]
    public void KillSwitchDisablesOnlyOnZero()
    {
        Assert.False(SmoothedActiveSet.IsRequested("0"));
        Assert.True(SmoothedActiveSet.IsRequested(null));
        Assert.True(SmoothedActiveSet.IsRequested("1"));
    }

    [Fact]
    public void RealGameMatchesPinnedIlAndAuditedWriters() => SmoothedActiveSet.ValidateNative();

    [Fact]
    public void AuditedSourcesAreTheVanillaNonDeterministicSources() =>
        Assert.Equal([typeof(PartToggleValueSelector), typeof(PartValueOperation)], SmoothedActiveSet.Sources);

    [Fact]
    public void HooksCoverTheRemoverAndEveryAuditedSetter()
    {
        List<MethodBase> dependencies = SmoothedActiveSet.Dependencies();
        Assert.All(SmoothedActiveSet.Targets, t => Assert.Contains(t.Target, dependencies));
        Assert.Equal(3, SmoothedActiveSet.Setters.Length);
        Assert.Contains(SmoothedActiveSet.Targets, t => t.Target == SmoothedActiveSet.RemoveTarget && t.Patch == typeof(SmoothedActiveSet.RemovalHook));
    }

    private sealed class Scene
    {
        public readonly PartSmoothedValue.SmoothedValueManager Manager = New<PartSmoothedValue.SmoothedValueManager>();
        public readonly List<PartSmoothedValue> List = [];
        public readonly IComponentValueProvider[] Sources = [New<PartToggleValueSelector>(), New<PartValueOperation>(), New<PartToggleValueSelector>(), New<PartSmoothedValue>()];
        public readonly PartSmoothedValue[] Values;

        public Scene()
        {
            AccessTools.Field(typeof(PartSmoothedValue.SmoothedValueManager), "_nonDeterministicValues").SetValue(Manager, List);
            // Real rules with an unmodified MaxDelta: the native UpdateValue reads it with no part (no buffs).
            var rules = New<PartSmoothedValueRules>();
            rules.MaxDelta = new ModifiableFloat(MaxDelta);
            Values = Sources.Select(source =>
            {
                var value = New<PartSmoothedValue>();
                RulesField.SetValue(value, rules);
                value._valueSource = source;
                return value;
            }).ToArray();
            List.AddRange(Values);
        }
    }

    /// <summary>Script of source changes, reentrant changes, list edits and source swaps; returns the value bits per frame.</summary>
    private static (string Trace, int Calls) Run(bool activeSet)
    {
        var scene = new Scene();
        var replacement = New<PartToggleValueSelector>();
        var log = new List<string>();
        _calls = 0;
        // Reentrant: updating value 0 while it moves changes the source of value 2 (later in the list).
        _onUpdate = v =>
        {
            if (ReferenceEquals(v, scene.Values[0]) && v.Value > 0.3f && v.Value < 0.6f) SetValue(scene.Sources[2], v.Value * 2f);
        };
        for (int frame = 0; frame < 40; frame++)
        {
            switch (frame)
            {
                case 1: SetValue(scene.Sources[0], 1f); break;
                case 3: SetValue(scene.Sources[1], -0.5f); break;
                case 8: SetValue(scene.Sources[3], 0.75f); break; // untracked source type
                case 12: scene.List.Remove(scene.Values[1]); break;
                case 14: SetValue(scene.Sources[1], 2f); scene.List.Add(scene.Values[1]); break;
                case 18: SetValue(replacement, 0.4f); scene.Values[2]._valueSource = replacement; break;
                case 22: SetValue(replacement, 0.1f); break;
                case 26: SetValue(scene.Sources[2], 9f); break; // old source: no longer read
                case 30: SetValue(scene.Values[0], float.NaN); break; // own value written
                case 33: scene.List.Add(scene.Values[0]); break; // duplicate entry, as native allows
            }
            float dt = frame == 5 ? 0f : 1f / 60f * (1 + frame % 3);
            if (activeSet) Assert.False(SmoothedActiveSet.Step(scene.Manager, dt));
            else foreach (PartSmoothedValue value in scene.List) value.UpdateValue(dt);
            log.Add(string.Join(",", scene.Values.Select(v => BitConverter.SingleToInt32Bits(v.Value).ToString("X8"))));
        }
        return (string.Join("\n", log), _calls);
    }

    [Fact]
    public void MatchesNativeValuesWithFewerCalls()
    {
        (string vanilla, int vanillaCalls) = Run(false);
        (string patched, int patchedCalls) = Run(true);
        Assert.Equal(vanilla, patched);
        Assert.True(patchedCalls < vanillaCalls / 2, $"{patchedCalls} vs {vanillaCalls}");
    }

    [Fact]
    public void ListChangeDuringUpdateThrowsLikeNativeEnumerator()
    {
        var scene = new Scene();
        _calls = 0;
        _onUpdate = v =>
        {
            if (ReferenceEquals(v, scene.Values[0])) scene.List.Remove(scene.Values[3]);
        };
        Assert.Throws<InvalidOperationException>(() => SmoothedActiveSet.Step(scene.Manager, 0.1f));
        Assert.Equal(1, _calls);
    }

    [Fact]
    public void SettledValuesSleepAndRemovedValuesUnsubscribe()
    {
        var scene = new Scene();
        _calls = 0;
        _onUpdate = null;
        SmoothedActiveSet.Step(scene.Manager, 0.1f);
        int first = _calls;
        SmoothedActiveSet.Step(scene.Manager, 0.1f);
        Assert.Equal(4, first);
        Assert.Equal(first + 1, _calls); // only the untracked source keeps polling
        Remove(scene.Manager, scene.Values[0]);
        Assert.Null(Handlers(scene.Sources[0]));
        Assert.Null(Handlers(scene.Values[0]));
        Assert.NotNull(Handlers(scene.Sources[1]));
    }

    [Fact]
    public void ThrowingEarlierSubscriberStillWakesTheValue()
    {
        var scene = new Scene();
        var source = (PartToggleValueSelector)scene.Sources[0];
        EventHandler<EventArgs> thrower = (_, _) => throw new InvalidOperationException("subscriber");
        source.ValueChanged += thrower; // runs before the entry's wake, which subscribes on the first Step
        SmoothedActiveSet.Step(scene.Manager, 0.1f);
        _calls = 0;
        SmoothedActiveSet.Step(scene.Manager, 0.1f);
        Assert.Equal(1, _calls); // value 0 sleeps: only the untracked source polls

        // The setter stores the value, then the earlier subscriber throws; the exception is not suppressed.
        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(() => SetValue(source, 1f));
        Assert.Equal("subscriber", Assert.IsType<InvalidOperationException>(ex.InnerException).Message);
        Assert.Equal(1f, source.Value);
        source.ValueChanged -= thrower;

        SmoothedActiveSet.Step(scene.Manager, 0.1f);
        Assert.Equal(1f, scene.Values[0].Value);
    }

    /// <summary>A (awake) changes sleeping B's source in its handler and catches the throw of an earlier subscriber.</summary>
    private static float CaughtThrowDuringStep(bool activeSet)
    {
        var scene = new Scene();
        PartSmoothedValue a = scene.Values[0], b = scene.Values[1];
        var bSource = (PartValueOperation)scene.Sources[1];
        bSource.ValueChanged += (_, _) => throw new InvalidOperationException("subscriber"); // before the entry's wake
        void Frame()
        {
            if (activeSet) Assert.False(SmoothedActiveSet.Step(scene.Manager, 0.01f));
            else foreach (PartSmoothedValue value in scene.List) value.UpdateValue(0.01f);
        }
        Frame(); // everything settles; B sleeps
        SetValue(scene.Sources[0], 1f); // A wakes
        bool done = false;
        _onUpdate = v =>
        {
            if (!ReferenceEquals(v, a) || done) return;
            done = true;
            Assert.Throws<TargetInvocationException>(() => SetValue(bSource, 0.5f));
        };
        Frame();
        Assert.True(done);
        Assert.Equal(0.5f, bSource.Value);
        return b.Value;
    }

    [Fact]
    public void CaughtSetterThrowWakesLaterValuesInTheSameStep()
    {
        float native = CaughtThrowDuringStep(false);
        Assert.NotEqual(0f, native);
        Assert.Equal(native, CaughtThrowDuringStep(true));
    }

    [Fact]
    public void RemovingTheFinalValueReleasesEverythingWithoutAnotherStep()
    {
        var scene = new Scene();
        SmoothedActiveSet.Step(scene.Manager, 0.1f);
        Assert.NotNull(Handlers(scene.Values[3]));
        // The last removal disables the manager's Update natively; nothing may depend on a later Step.
        foreach (PartSmoothedValue value in scene.Values) Remove(scene.Manager, value);
        Assert.Empty(scene.List);
        Assert.All(scene.Sources, s => Assert.Null(Handlers(s)));
        Assert.All(scene.Values, v => Assert.Null(Handlers(v)));
    }

    [Fact]
    public void DisableReleasesEverySubscription()
    {
        var scene = new Scene();
        SmoothedActiveSet.Step(scene.Manager, 0.1f);
        Assert.NotNull(Handlers(scene.Sources[0]));
        SmoothedActiveSet.Disable();
        Assert.All(scene.Sources, s => Assert.Null(Handlers(s)));
        Assert.All(scene.Values, v => Assert.Null(Handlers(v)));
        // A later Step (feature re-enabled) rebuilds from the native list.
        SetValue(scene.Sources[0], 1f);
        SmoothedActiveSet.Step(scene.Manager, 0.1f);
        Assert.Equal(1f, scene.Values[0].Value);
        Assert.NotNull(Handlers(scene.Sources[0]));
    }
}
