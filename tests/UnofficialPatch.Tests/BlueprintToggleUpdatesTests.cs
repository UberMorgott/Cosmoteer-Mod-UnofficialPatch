using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer.MPInputs;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Blueprints;
using Cosmoteer.Ships.Blueprints.Logic;
using Cosmoteer.Ships.Blueprints.Logic.Values;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Logic;
using Cosmoteer.Ships.Parts.UI;
using Cosmoteer.Source.Ships.Blueprints;
using Halfling.Graphics;
using Halfling.Scene2D;
using HarmonyLib;
using Xunit;

namespace UnofficialPatch.Tests;

public class BlueprintToggleUpdatesTests : IDisposable
{
    private const string ForeignId = "performance.tests.foreign-blueprint-updates";
    private readonly Harmony _foreign = new(ForeignId);
    private readonly Func<Assembly, string?> _scan = BlueprintToggleUpdates.Scan;

    // This assembly implements and writes toggle states on purpose (fixtures); the real game and mod are scanned.
    public BlueprintToggleUpdatesTests() =>
        BlueprintToggleUpdates.Scan = assembly => assembly == typeof(BlueprintToggleUpdatesTests).Assembly ? null : _scan(assembly);

    public void Dispose()
    {
        BlueprintToggleEvaluation.EndValidatedUpdate();
        BlueprintToggleUpdates.Scan = _scan;
        _foreign.UnpatchAll(ForeignId);
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void OnUnlessExplicitlyDisabled(string? value, bool expected) => Assert.Equal(expected, BlueprintToggleUpdates.IsRequested(value));

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, false, false, false, true)]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, false, true, false, true)]
    [InlineData(true, false, false, true, true)]
    public void SkipsOnlyUnchangedUnobservedFrames(bool unchanged, bool overridden, bool selected, bool build, bool native) =>
        Assert.Equal(native, BlueprintToggleUpdates.MustRunNative(unchanged, overridden, selected, build));

    [Fact]
    public void NativeIlMatchesPinnedHash() => BlueprintToggleUpdates.ValidateNative();

    [Fact]
    public void CallbackListVersionTracksMutations() => BlueprintToggleUpdates.ValidateListVersion();

    [Fact]
    public void LoaderPatchTargetsAreNotReadDependencies()
    {
        BlueprintToggleUpdates.ValidateNative();
        // The loader owner is foreign to the per-frame check: a pinned loader target here would pause skipping forever.
        Assert.All(KnownLoaderPatches.NativeTargets(), target => Assert.False(BlueprintToggleUpdates.IsDependency(target), target.Name));
        Assert.True(BlueprintToggleUpdates.IsDependency(BlueprintToggleUpdates.Target));
        Assert.True(BlueprintToggleUpdates.IsDependency(PortToggleMode.ToggleModes[0]));
    }

    [Fact]
    public void ReadClosureCoversCallbacksTogglesAndNoNetworkRebuild()
    {
        List<MethodBase> closure = BlueprintToggleUpdates.ReadClosure();
        foreach ((_, MethodInfo callback) in BlueprintToggleUpdates.Callbacks) Assert.Contains(callback, closure);
        Assert.Contains(closure, m => m.Name == "GetToggleMode" && m.DeclaringType == typeof(BlueprintPartStatProvider));
        Assert.Contains(closure, m => m.Name == "IsBlueprintToggleOn" && m.DeclaringType == typeof(PartMultiToggleRules));
        Assert.Contains(closure, m => m.Name.EndsWith("GetBlueprintMode", StringComparison.Ordinal) && m.DeclaringType == typeof(PartStaticModeRules));
        Assert.DoesNotContain(closure, m => m.Name == "RequestPortRegistration");
    }

    [Fact]
    public void GameWriteSitesAreExactlyTheVettedHooks()
    {
        var watch = Stopwatch.StartNew();
        Assert.Null(BlueprintToggleUpdates.ScanAssembly(typeof(Ship).Assembly));
        Assert.True(watch.ElapsedMilliseconds < 5000, "scan took " + watch.ElapsedMilliseconds + " ms");
        // Every writer except the local-copy WriteTo is hooked.
        MethodBase[] hooked = BlueprintToggleUpdates.Targets.Skip(1).Select(t => t.Target).ToArray();
        Assert.Equal(BlueprintToggleUpdates.Writers.Where(w => w.Name != nameof(ShipMetadata.WriteTo)), hooked);
    }

    [Fact]
    public void ScanRefusesForeignWritersAndImplementations()
    {
        Assert.True(BlueprintToggleUpdates.WritesToggleStates(AccessTools.Method(typeof(BlueprintToggleUpdatesTests), nameof(ForeignWrite))));
        Assert.True(BlueprintToggleUpdates.WritesToggleStates(AccessTools.Method(typeof(BlueprintToggleUpdatesTests), nameof(OpenWrite))));
        Assert.False(BlueprintToggleUpdates.WritesToggleStates(AccessTools.Method(typeof(BlueprintToggleUpdatesTests), nameof(ForeignRead))));
        Assert.False(BlueprintToggleUpdates.WritesToggleStates(AccessTools.Method(typeof(BlueprintToggleUpdatesTests), nameof(ColorWrite))));
        // This assembly also implements IBlueprintComponentToggle (see BlueprintToggleEvaluationTests).
        Assert.NotNull(BlueprintToggleUpdates.ScanAssembly(typeof(BlueprintToggleUpdatesTests).Assembly));
        Assert.Null(BlueprintToggleUpdates.ScanAssembly(typeof(BlueprintToggleUpdates).Assembly));
        Assert.Null(BlueprintToggleUpdates.ScanAssembly(typeof(object).Assembly));
        // Interface bodies (static and default methods) are scanned too.
        Assert.Contains("toggle-state write", BlueprintToggleUpdates.ScanTypes(typeof(IStateWriter).Assembly, [typeof(IStateWriter)]));
    }

    [Theory]
    [InlineData(typeof(DefaultWriter))]
    [InlineData(typeof(ChoicesWriter))]
    [InlineData(typeof(StaticToggleWriter))]
    [InlineData(typeof(MultiToggleWriter))]
    [InlineData(typeof(SourceWriter))]
    public void ScanRefusesRulesDataWriters(Type type) =>
        Assert.Contains("rules data access", BlueprintToggleUpdates.ScanTypes(type.Assembly, [type]));

    [Fact]
    public void ScanAllowsPlainRulesReads() => Assert.Null(BlueprintToggleUpdates.ScanTypes(typeof(RulesReader).Assembly, [typeof(RulesReader)]));

    [Fact]
    public void ScanRefusesUnknownDynamicAssemblies()
    {
        Assert.NotNull(BlueprintToggleUpdates.ScanAssembly(AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("UnofficialPatch.Tests.Dynamic"), AssemblyBuilderAccess.Run)));
        Assert.Null(BlueprintToggleUpdates.ScanAssembly(AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DMDASM.UnofficialPatch.Tests"), AssemblyBuilderAccess.Run)));
        // Whatever Harmony created while patching in this process is known.
        Assert.True(Patcher.Apply());
        new Harmony(Patcher.HarmonyId).UnpatchAll(Patcher.HarmonyId);
        Assert.All(AppDomain.CurrentDomain.GetAssemblies().Where(a => a.IsDynamic && !a.GetName().Name!.StartsWith("UnofficialPatch.Tests", StringComparison.Ordinal) && !ViewDetectTests.IsForeignFixture(a)),
            a => Assert.True(BlueprintToggleUpdates.IsKnownDynamic(a), a.GetName().Name));
    }

    [Fact]
    public void ChangedGameRollsBackEveryPatchButForeignCodeOnlyPausesTheFeature()
    {
        Func<Assembly, string?> scan = BlueprintToggleUpdates.Scan;
        try
        {
            BlueprintToggleUpdates.Scan = assembly => assembly == typeof(Ship).Assembly ? "game changed" : scan(assembly);
            Assert.False(Patcher.Apply());
            Assert.False(Harmony.HasAnyPatches(Patcher.HarmonyId));

            BlueprintToggleUpdates.Scan = assembly => assembly == typeof(BlueprintToggleUpdatesTests).Assembly ? "foreign writer" : scan(assembly);
            Assert.True(Patcher.Apply());
            Assert.False(BlueprintToggleUpdates.Ready);
            Assert.Equal("foreign writer", BlueprintToggleUpdates.FailureReason);
            Assert.True(Harmony.HasAnyPatches(Patcher.HarmonyId));
        }
        finally
        {
            BlueprintToggleUpdates.Scan = scan;
            new Harmony(Patcher.HarmonyId).UnpatchAll(Patcher.HarmonyId);
        }
    }

    [Fact]
    public void NativeCallbacksRerunAfterWritesAndAfterForeignPatches()
    {
        try
        {
            Assert.True(Patcher.Apply());
            Assert.True(BlueprintToggleUpdates.Ready, BlueprintToggleUpdates.FailureReason);
            var fixture = new Fixture();
            var rules = Empty<PartUIToggleRules>();
            var partRules = Empty<PartRules>();
            AccessTools.Field(typeof(PartRules), nameof(PartRules.UIToggleComponentsByID))
                .SetValue(partRules, new Dictionary<ID<PartToggleGuiRules>, PartUIToggleRules> { [default] = rules });
            var toggle = Fixture.Component<BlueprintPartNetworkToggle>(fixture.Ship);
            BlueprintPart part = toggle.Part!;
            SetAuto(part, typeof(CommonBasePart), "Rules", partRules);
            AccessTools.Field(typeof(BlueprintPartNetworkToggle), "_rulesToggleSource").SetValue(toggle, rules);
            int changes = 0;
            toggle.ToggleChanged += (_, _) => changes++;
            var callbacks = new PartsManager.UpdateCallbacks(BlueprintToggleUpdates.Bucket);
            callbacks.Callbacks.Add(Callback(toggle));
            void Update() => BlueprintToggleUpdates.Target.Invoke(callbacks, [null]); // the real, patched method

            BlueprintToggleEvaluation.BeginValidatedUpdate();
            Update(); // native: default mode 0 is off
            Assert.False(toggle.IsToggleOn);
            // Rules data is immutable after loading (writers are refused by the scan); changing it here shows the skip.
            rules.Default = 1;
            Update();
            Assert.False(toggle.IsToggleOn);
            rules.Default = 0;

            // A hooked write re-runs native: the multiplayer input writes, then throws on the fake ship's part lookup.
            var input = new MPSetUIToggleMetadataInput(fixture.Ship, new PartSaveData(part), default, 1, blueprintMode: true);
            Assert.ThrowsAny<Exception>(void () => input.Execute(null!, 0, default, force: true));
            Update();
            Assert.True(toggle.IsToggleOn);
            Update();
            Assert.Equal(1, changes);

            // A foreign patch on a read dependency: unvalidated native runs and changes the output...
            _foreign.Patch(AccessTools.Method(typeof(BlueprintPartNetworkToggle), "GetToggleMode"),
                postfix: new HarmonyMethod(typeof(BlueprintToggleUpdatesTests), nameof(ForceModeOff)));
            BlueprintToggleEvaluation.BeginValidatedUpdate();
            Assert.False(BlueprintToggleUpdates.IsFrameValidated);
            Update();
            Assert.False(toggle.IsToggleOn);
            // ...so once it is removed, the first validated frame runs native again and restores the vanilla output.
            _foreign.UnpatchAll(ForeignId);
            BlueprintToggleEvaluation.BeginValidatedUpdate();
            Assert.True(BlueprintToggleUpdates.IsFrameValidated);
            Update();
            Assert.True(toggle.IsToggleOn);
            Update();
            Assert.Equal(3, changes);
        }
        finally
        {
            BlueprintToggleEvaluation.EndValidatedUpdate();
            new Harmony(Patcher.HarmonyId).UnpatchAll(Patcher.HarmonyId);
        }
    }

    private static void ForceModeOff(ref int? __result) => __result = __result.HasValue ? 0 : null; // stored modes read as off

    [Fact]
    public void MutatorsAreTheToggleInstantiationOnly()
    {
        Type toggles = typeof(PartStateDictionary<(PartSaveData, ID<PartToggleGuiRules>), int>);
        Type colors = typeof(PartStateDictionary<(PartSaveData, ID<PartColorGuiRules>), Color>);
        Assert.True(BlueprintToggleUpdates.IsMutator(toggles.GetMethod("Set")));
        Assert.True(BlueprintToggleUpdates.IsMutator(toggles.GetMethod("RemoveAll")));
        Assert.True(BlueprintToggleUpdates.IsMutator(toggles.GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)));
        Assert.True(BlueprintToggleUpdates.IsMutator(typeof(PartStateDictionary<,>).GetMethod("Clear")));
        Assert.False(BlueprintToggleUpdates.IsMutator(toggles.GetMethod("Get")));
        Assert.False(BlueprintToggleUpdates.IsMutator(colors.GetMethod("Set")));
    }

    [Fact]
    public void WhitelistAcceptsOnlyVettedSingleTargetCallbacksOfOneShip()
    {
        var fixture = new Fixture();
        Assert.Same(fixture.Ship, BlueprintToggleUpdates.VerifiedShip(fixture.List));
        Assert.Null(BlueprintToggleUpdates.VerifiedShip([]));

        var stat = Fixture.Component<BlueprintPartStatProvider>(fixture.Ship);
        Assert.Same(fixture.Ship, BlueprintToggleUpdates.VerifiedShip([.. fixture.List, Callback(stat)]));
        var other = Fixture.Component<BlueprintPartStatProvider>(Empty<Ship>());
        Assert.Null(BlueprintToggleUpdates.VerifiedShip([.. fixture.List, Callback(other)]));

        SceneComponent.UpdateCallback multicast = fixture.List[0] + Callback(stat);
        Assert.Null(BlueprintToggleUpdates.VerifiedShip([multicast]));
        Assert.Null(BlueprintToggleUpdates.VerifiedShip([_ => { }]));

        AccessTools.Field(typeof(BaseBlueprintPartNetworkPort), "_rulesOperationalToggle").SetValue(fixture.Port, Empty<SubclassToggle>());
        Assert.Null(BlueprintToggleUpdates.VerifiedShip(fixture.List));
        AccessTools.Field(typeof(BaseBlueprintPartNetworkPort), "_rulesOperationalToggle").SetValue(fixture.Port, Empty<PartMultiToggleRules>());
        Assert.Same(fixture.Ship, BlueprintToggleUpdates.VerifiedShip(fixture.List));
        AccessTools.Field(typeof(BaseBlueprintPartNetworkPort), "_rulesOperationalToggle").SetValue(fixture.Port, null);
        Assert.Null(BlueprintToggleUpdates.VerifiedShip(fixture.List));
    }

    [Fact]
    public void GateRunsNativeExactlyWhenAnInputChanged()
    {
        try
        {
            Assert.True(Patcher.Apply());
            Assert.True(BlueprintToggleUpdates.Ready, BlueprintToggleUpdates.FailureReason);
            var fixture = new Fixture();
            PartsManager.UpdateCallbacks callbacks = fixture.Callbacks();

            Assert.True(Run(callbacks)); // interval closed: native
            BlueprintToggleEvaluation.BeginValidatedUpdate();
            Assert.True(BlueprintToggleUpdates.IsFrameValidated);
            Assert.True(Run(callbacks)); // first frame
            Assert.False(Run(callbacks));
            Assert.False(Run(callbacks));

            BlueprintToggleUpdates.Bump(fixture.Ship);
            Assert.True(Run(callbacks));
            Assert.False(Run(callbacks));
            BlueprintToggleUpdates.Bump(Empty<Ship>()); // other ship
            Assert.False(Run(callbacks));
            BlueprintToggleUpdates.Bump(null);
            Assert.True(Run(callbacks));
            Assert.False(Run(callbacks));

            callbacks.Callbacks.Add(Callback(Fixture.Component<BlueprintPartStatProvider>(fixture.Ship)));
            Assert.True(Run(callbacks));
            Assert.False(Run(callbacks));
            callbacks.Callbacks[1] = callbacks.Callbacks[1];
            Assert.True(Run(callbacks));
            Assert.False(Run(callbacks));

            SetAuto(fixture.Manager, "GetToggleModeOverride", new Func<Ship, PartInfo, ID<PartToggleGuiRules>, int>((_, _, _) => 0));
            Assert.True(Run(callbacks));
            Assert.True(Run(callbacks));
            SetAuto(fixture.Manager, "GetToggleModeOverride", null);
            Assert.True(Run(callbacks)); // first frame after the override ended
            Assert.False(Run(callbacks));

            SetAuto(fixture.Metadata, "PartUIToggleStates", new PartStateDictionary<(PartSaveData, ID<PartToggleGuiRules>), int>());
            Assert.True(Run(callbacks));
            Assert.False(Run(callbacks));

            // A native run that threw repeats next frame.
            BlueprintToggleUpdates.Bump(fixture.Ship);
            Assert.True(BlueprintToggleUpdates.Prefix(callbacks, out bool state));
            Assert.True(state);
            BlueprintToggleUpdates.Finalizer(callbacks, state, new InvalidOperationException("native"));
            Assert.True(Run(callbacks));
            Assert.False(Run(callbacks));

            callbacks.Callbacks.Add(_ => { });
            Assert.True(Run(callbacks));
            Assert.True(Run(callbacks));

            BlueprintToggleEvaluation.EndValidatedUpdate();
            callbacks.Callbacks.RemoveAt(2);
            BlueprintToggleEvaluation.BeginValidatedUpdate();
            Assert.True(Run(callbacks));
            Assert.False(Run(callbacks));

            // Other buckets are never skipped.
            var other = new PartsManager.UpdateCallbacks(5);
            other.Callbacks.AddRange(callbacks.Callbacks);
            Assert.True(Run(other));
            Assert.True(Run(other));
        }
        finally
        {
            BlueprintToggleEvaluation.EndValidatedUpdate();
            new Harmony(Patcher.HarmonyId).UnpatchAll(Patcher.HarmonyId);
        }
    }

    [Fact]
    public void WriteHooksBumpAfterRealWrites()
    {
        try
        {
            Assert.True(Patcher.Apply());
            var fixture = new Fixture();
            PartsManager.UpdateCallbacks callbacks = fixture.Callbacks();
            BlueprintToggleEvaluation.BeginValidatedUpdate();
            Assert.True(Run(callbacks));
            Assert.False(Run(callbacks));
            SetAuto(fixture.Metadata, typeof(SimpleShipComponent), "Ship", fixture.Ship);
            SetAuto(fixture.Metadata, "PartUIColorValues", new PartStateDictionary<(PartSaveData, ID<PartColorGuiRules>), Color>());
            fixture.Metadata.ApplyAllBlueprintStates();
            Assert.True(Run(callbacks));
            Assert.False(Run(callbacks));
        }
        finally
        {
            BlueprintToggleEvaluation.EndValidatedUpdate();
            new Harmony(Patcher.HarmonyId).UnpatchAll(Patcher.HarmonyId);
        }
    }

    [Fact]
    public void ForeignReadDependencyPatchPausesSkipping()
    {
        try
        {
            Assert.True(Patcher.Apply());
            var fixture = new Fixture();
            PartsManager.UpdateCallbacks callbacks = fixture.Callbacks();
            BlueprintToggleEvaluation.BeginValidatedUpdate();
            Assert.True(Run(callbacks));
            Assert.False(Run(callbacks));
            _foreign.Patch(AccessTools.Method(typeof(PartStaticToggleRules), "Cosmoteer.Ships.Parts.Logic.IBlueprintComponentToggle.IsBlueprintToggleOn"),
                prefix: new HarmonyMethod(typeof(BlueprintToggleUpdatesTests), nameof(BeforeDependency)));
            BlueprintToggleEvaluation.BeginValidatedUpdate();
            Assert.False(BlueprintToggleUpdates.IsFrameValidated);
            Assert.True(BlueprintToggleEvaluation.IsValidatedUpdate); // leaf specialization does not read it
            Assert.True(Run(callbacks));
            _foreign.UnpatchAll(ForeignId);
            BlueprintToggleEvaluation.BeginValidatedUpdate();
            Assert.True(BlueprintToggleUpdates.IsFrameValidated);
            Assert.True(Run(callbacks)); // the unvalidated run's output is re-derived once
            Assert.False(Run(callbacks));
        }
        finally
        {
            BlueprintToggleEvaluation.EndValidatedUpdate();
            new Harmony(Patcher.HarmonyId).UnpatchAll(Patcher.HarmonyId);
        }
    }

    private static bool Run(PartsManager.UpdateCallbacks callbacks)
    {
        bool native = BlueprintToggleUpdates.Prefix(callbacks, out bool state);
        BlueprintToggleUpdates.Finalizer(callbacks, state, null);
        return native;
    }

    private static void BeforeDependency() { }

    private static void ForeignWrite(ShipMetadata metadata) => metadata.PartUIToggleStates.Set(default, 1);
    private static void OpenWrite<T>(PartStateDictionary<(PartSaveData, T), int> states) where T : notnull => states.Clear();
    private static int? ForeignRead(ShipMetadata metadata) => metadata.PartUIToggleStates.Get(default, true);
    private static void ColorWrite(ShipMetadata metadata) => metadata.PartUIColorValues.Set(default, default(Color));

    private static SceneComponent.UpdateCallback Callback(BlueprintPartComponent component) =>
        AccessTools.Method(component.GetType(), "OnUpdate").CreateDelegate<SceneComponent.UpdateCallback>(component);

    private sealed class SubclassToggle : PartUIToggleRules;

    internal interface IStateWriter
    {
        static void Write(ShipMetadata metadata) => metadata.PartUIToggleStates.Set(default, 1);
    }

    internal static class DefaultWriter
    {
        public static void Write(PartUIToggleRules rules) => rules.Default = 1;
    }

    internal static class ChoicesWriter
    {
        public static void Write(PartUIToggleRules rules) => rules.ToggledOnChoices!.Add(1);
    }

    internal static class StaticToggleWriter
    {
        public static void Write(PartStaticToggleRules rules) => rules.ToggleOn = true;
    }

    internal static class MultiToggleWriter
    {
        public static void Write(PartMultiToggleRules rules) => rules.Toggles![0].Invert = true;
    }

    internal static class SourceWriter
    {
        public static void Write(BlueprintPartNetworkToggle toggle) => toggle._rulesToggleSource = null!;
    }

    internal static class RulesReader
    {
        public static int Read(PartUIToggleRules rules, PartStaticToggleRules on) => on.ToggleOn ? rules.Default : 0;
    }

    private static T Empty<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void SetAuto(object owner, string name, object? value) => SetAuto(owner, owner.GetType(), name, value);
    private static void SetAuto(object owner, Type type, string name, object? value) => AccessTools.Field(type, "<" + name + ">k__BackingField").SetValue(owner, value);

    private sealed class Fixture
    {
        public readonly Ship Ship = Empty<Ship>();
        public readonly ShipMetadata Metadata = Empty<ShipMetadata>();
        public readonly BlueprintPartsManager Manager = Empty<BlueprintPartsManager>();
        public readonly BlueprintPartNetworkPort Port;
        public readonly List<SceneComponent.UpdateCallback> List;

        public Fixture()
        {
            SetAuto(Ship, "BlueprintParts", Manager);
            SetAuto(Ship, "Metadata", Metadata);
            SetAuto(Metadata, "PartUIToggleStates", new PartStateDictionary<(PartSaveData, ID<PartToggleGuiRules>), int>());
            Port = Component<BlueprintPartNetworkPort>(Ship);
            AccessTools.Field(typeof(BaseBlueprintPartNetworkPort), "_rulesOperationalToggle").SetValue(Port, Empty<PartUIToggleRules>());
            List = [AccessTools.Method(typeof(BaseBlueprintPartNetworkPort), "OnUpdate").CreateDelegate<SceneComponent.UpdateCallback>(Port)];
        }

        public PartsManager.UpdateCallbacks Callbacks()
        {
            var callbacks = new PartsManager.UpdateCallbacks(BlueprintToggleUpdates.Bucket);
            callbacks.Callbacks.AddRange(List);
            return callbacks;
        }

        public static T Component<T>(Ship ship) where T : BlueprintPartComponent
        {
            T component = Empty<T>();
            var part = Empty<BlueprintPart>();
            AccessTools.Field(typeof(CommonBasePart), "p_ship").SetValue(part, ship);
            SetAuto(component, typeof(BlueprintPartComponent), "Part", part);
            return component;
        }
    }
}
