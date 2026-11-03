using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Blueprints;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Logic;
using Cosmoteer.Ships.Parts.UI;
using Cosmoteer.Source.Ships.Blueprints;
using HarmonyLib;
using Halfling.Geometry;
using Xunit;

namespace UnofficialPatch.Tests;

public class BlueprintToggleEvaluationTests : IDisposable
{
    private const string ForeignId = "performance.tests.foreign-blueprint-leaf";
    private readonly Harmony _foreign = new(ForeignId);

    public void Dispose()
    {
        BlueprintToggleEvaluation.EndValidatedUpdate();
        _foreign.UnpatchAll(ForeignId);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void IntervalIsClosedByDefaultAndAtEnd()
    {
        BlueprintToggleEvaluation.EndValidatedUpdate();
        Assert.False(BlueprintToggleEvaluation.IsValidatedUpdate);
        BlueprintToggleEvaluation.BeginValidatedUpdate();
        Assert.True(BlueprintToggleEvaluation.IsValidatedUpdate);
        BlueprintToggleEvaluation.EndValidatedUpdate();
        Assert.False(BlueprintToggleEvaluation.IsValidatedUpdate);
    }

    [Fact]
    public void DependenciesMatchInstalledNativeIl() => BlueprintToggleEvaluation.ValidateNativeDependencies();

    [Fact]
    public void GenericInstantiationsMatchTheSameNativeDefinition()
    {
        MethodInfo a = AccessTools.Method(typeof(ID<PartRules>), nameof(GetHashCode), Type.EmptyTypes);
        MethodInfo b = AccessTools.Method(typeof(ID<PartColorGuiRules>), nameof(GetHashCode), Type.EmptyTypes);
        Assert.NotEqual(a.DeclaringType, b.DeclaringType);
        Assert.True(BlueprintToggleEvaluation.SameMethodDefinition(a, b));
        Assert.False(BlueprintToggleEvaluation.SameMethodDefinition(a, PortToggleMode.ToggleModes[0]));
    }

    [Fact]
    public void ValidatedIntervalDoesNotEnableOtherThreads()
    {
        BlueprintToggleEvaluation.BeginValidatedUpdate();
        Assert.True(BlueprintToggleEvaluation.IsValidatedUpdate);
        bool enabledOnWorker = true;
        var worker = new Thread(() => enabledOnWorker = BlueprintToggleEvaluation.IsValidatedUpdate);
        worker.Start();
        worker.Join();
        Assert.False(enabledOnWorker);
    }

    [Fact]
    public void LeafReadsLiveChoicesAndOverrideMutationOnce()
    {
        var fixture = new Fixture();
        int calls = 0;
        ID<PartToggleGuiRules> seen = default;
        SetAuto(fixture.Manager, "GetToggleModeOverride", new Func<Ship, PartInfo, ID<PartToggleGuiRules>, int>((_, _, id) =>
        {
            calls++;
            seen = id;
            fixture.Leaf.ToggledOnChoices = [7];
            return 7;
        }));
        BlueprintToggleEvaluation.BeginValidatedUpdate();
        Assert.True(BlueprintToggleEvaluation.Evaluate(fixture.Port));
        Assert.Equal(1, calls);
        fixture.Leaf.ToggleID = new ID<PartToggleGuiRules>("performance.test-toggle");
        Assert.True(BlueprintToggleEvaluation.Evaluate(fixture.Port));
        Assert.Equal(fixture.Leaf.ToggleID, seen);
        fixture.Leaf.ToggleID = default;
        fixture.Leaf.ToggledOnChoices = [];
        SetAuto(fixture.Manager, "GetToggleModeOverride", null);
        Assert.False(BlueprintToggleEvaluation.Evaluate(fixture.Port));
        fixture.Leaf.ToggledOnChoices = null;
        fixture.Leaf.Default = 0;
        Assert.False(BlueprintToggleEvaluation.Evaluate(fixture.Port));
        fixture.Leaf.Default = 3;
        Assert.True(BlueprintToggleEvaluation.Evaluate(fixture.Port));
    }

    [Fact]
    public void ForeignLeafPatchDisablesNextIntervalAndRunsFallback()
    {
        var fixture = new Fixture();
        BlueprintToggleEvaluation.BeginValidatedUpdate();
        Assert.True(BlueprintToggleEvaluation.IsValidatedUpdate);
        BlueprintToggleEvaluation.EndValidatedUpdate();
        _foreign.Patch(AccessTools.Method(typeof(PartUIToggleRules), "IsBlueprintToggleOn"), prefix: new HarmonyMethod(typeof(BlueprintToggleEvaluationTests), nameof(LeafOff)));
        BlueprintToggleEvaluation.BeginValidatedUpdate();
        Assert.False(BlueprintToggleEvaluation.IsValidatedUpdate);
        Assert.False(BlueprintToggleEvaluation.Evaluate(fixture.Port));
        BlueprintToggleEvaluation.EndValidatedUpdate();
        _foreign.UnpatchAll(ForeignId);
        BlueprintToggleEvaluation.BeginValidatedUpdate();
        Assert.True(BlueprintToggleEvaluation.IsValidatedUpdate);
    }

    [Fact]
    public void ForeignGetModeNullResultKeepsLiveLeafDefault()
    {
        var fixture = new Fixture();
        _foreign.Patch(PortToggleMode.ToggleModes[0], prefix: new HarmonyMethod(typeof(BlueprintToggleEvaluationTests), nameof(NullMode)));
        BlueprintToggleEvaluation.BeginValidatedUpdate();
        Assert.False(BlueprintToggleEvaluation.IsValidatedUpdate);
        fixture.Leaf.Default = 0;
        Assert.False(BlueprintToggleEvaluation.Evaluate(fixture.Port));
        fixture.Leaf.Default = 9;
        Assert.True(BlueprintToggleEvaluation.Evaluate(fixture.Port));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ForeignNativeReadDependencyClosesInterval(int dependency)
    {
        MethodBase method = dependency switch
        {
            0 => AccessTools.PropertyGetter(typeof(BlueprintPartComponent), "Part"),
            1 => AccessTools.PropertyGetter(typeof(Ship), "Metadata"),
            2 => AccessTools.PropertyGetter(typeof(ShipMetadata), "PartUIToggleStates"),
            _ => AccessTools.Constructor(typeof(PartSaveData), [typeof(CommonBasePart)]),
        };
        _foreign.Patch(method, prefix: new HarmonyMethod(typeof(BlueprintToggleEvaluationTests), nameof(BeforeDependency)));
        BlueprintToggleEvaluation.BeginValidatedUpdate();
        Assert.False(BlueprintToggleEvaluation.IsValidatedUpdate);
    }

    [Fact]
    public void ForeignExplicitGeometryComparerClosesInterval()
    {
        MethodInfo comparer = typeof(IntVector2).GetInterfaceMap(typeof(IEquatable<IntVector2>)).TargetMethods[0];
        _foreign.Patch(comparer, prefix: new HarmonyMethod(typeof(BlueprintToggleEvaluationTests), nameof(BeforeDependency)));
        BlueprintToggleEvaluation.BeginValidatedUpdate();
        Assert.False(BlueprintToggleEvaluation.IsValidatedUpdate);
    }

    [Fact]
    public void SubclassUsesItsInterfaceImplementationOutsideExactLeaf()
    {
        var fixture = new Fixture();
        AccessTools.Field(typeof(BaseBlueprintPartNetworkPort), "_rulesOperationalToggle").SetValue(fixture.Port, Empty<SubclassToggle>());
        BlueprintToggleEvaluation.BeginValidatedUpdate();
        Assert.False(BlueprintToggleEvaluation.Evaluate(fixture.Port));
    }

    [Fact]
    public void CustomRuleAndNullPartKeepFallbackAndReadOrder()
    {
        var fixture = new Fixture();
        var custom = new CustomToggle();
        AccessTools.Field(typeof(BaseBlueprintPartNetworkPort), "_rulesOperationalToggle").SetValue(fixture.Port, custom);
        BlueprintToggleEvaluation.BeginValidatedUpdate();
        Assert.True(BlueprintToggleEvaluation.Evaluate(fixture.Port));
        Assert.Equal(1, custom.Calls);
        SetAuto(fixture.Port, typeof(BlueprintPartComponent), "Part", null);
        Assert.Throws<NullReferenceException>(() => BlueprintToggleEvaluation.Evaluate(fixture.Port));
        Assert.Equal(1, custom.Calls);
    }

    [Fact]
    public void WarmLeafEvaluationAllocatesNothingPerCall()
    {
        var fixture = new Fixture();
        BlueprintToggleEvaluation.BeginValidatedUpdate();
        Assert.True(BlueprintToggleEvaluation.IsValidatedUpdate);
        for (int i = 0; i < 10000; i++) _ = BlueprintToggleEvaluation.Evaluate(fixture.Port);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) _ = BlueprintToggleEvaluation.Evaluate(fixture.Port);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated);
    }

    private static bool LeafOff(out bool __result)
    {
        __result = false;
        return false;
    }

    private static bool NullMode(out int? __result)
    {
        __result = null;
        return false;
    }

    private static void BeforeDependency() { }

    private sealed class SubclassToggle : PartUIToggleRules, IBlueprintComponentToggle
    {
        bool IBlueprintComponentToggle.IsBlueprintToggleOn(PartRules rules, Func<ID<PartToggleGuiRules>, int?> getToggleMode) => false;
    }

    private sealed class CustomToggle : IBlueprintComponentToggle
    {
        public int Calls;
        public bool IsBlueprintToggleOn(PartRules rules, Func<ID<PartToggleGuiRules>, int?> getToggleMode)
        {
            Calls++;
            return true;
        }
    }

    private static T Empty<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void SetAuto(object owner, string name, object? value) => SetAuto(owner, owner.GetType(), name, value);
    private static void SetAuto(object owner, Type type, string name, object? value) => AccessTools.Field(type, "<" + name + ">k__BackingField").SetValue(owner, value);

    private sealed class Fixture
    {
        public readonly BaseBlueprintPartNetworkPort Port = Empty<BlueprintPartNetworkPort>();
        public readonly PartUIToggleRules Leaf = Empty<PartUIToggleRules>();
        public readonly BlueprintPartsManager Manager = Empty<BlueprintPartsManager>();

        public Fixture()
        {
            var part = Empty<BlueprintPart>();
            var rules = Empty<PartRules>();
            var ship = Empty<Ship>();
            var metadata = Empty<ShipMetadata>();
            Leaf.Default = 1;
            AccessTools.Field(typeof(PartRules), "UIToggleComponentsByID").SetValue(rules, new Dictionary<ID<PartToggleGuiRules>, PartUIToggleRules> { [default] = Leaf });
            SetAuto(part, typeof(CommonBasePart), "Rules", rules);
            AccessTools.Field(typeof(CommonBasePart), "p_ship").SetValue(part, ship);
            SetAuto(Port, typeof(BlueprintPartComponent), "Part", part);
            SetAuto(ship, "BlueprintParts", Manager);
            SetAuto(ship, "Metadata", metadata);
            SetAuto(metadata, "PartUIToggleStates", new PartStateDictionary<(PartSaveData, ID<PartToggleGuiRules>), int>());
            AccessTools.Field(typeof(BaseBlueprintPartNetworkPort), "_rulesOperationalToggle").SetValue(Port, Leaf);
        }
    }
}
