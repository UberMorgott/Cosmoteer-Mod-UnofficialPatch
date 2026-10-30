using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Halfling.Graphics;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

public sealed class AtlasUploadModeTests
{
    private const string TestHarmonyId = "performance.tests.atlas-upload-mode";
    private static PerformanceMode _observedMode;

    [Theory]
    [InlineData(false, PerformanceMode.Dynamic, PerformanceMode.Dynamic)]
    [InlineData(true, PerformanceMode.Dynamic, PerformanceMode.Static)]
    [InlineData(true, PerformanceMode.Static, PerformanceMode.Static)]
    [InlineData(true, PerformanceMode.Immutable, PerformanceMode.Immutable)]
    [InlineData(true, PerformanceMode.DynamicStreaming, PerformanceMode.DynamicStreaming)]
    [InlineData(true, (PerformanceMode)(-1), (PerformanceMode)(-1))]
    public void SelectorOnlyConvertsOptedInDynamicMode(bool enabled, PerformanceMode input, PerformanceMode expected) =>
        Assert.Equal(expected, AtlasUploadMode.Select(enabled, input));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("true", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("2", true)]
    [InlineData("3", true)]
    [InlineData("4", false)]
    public void OptInRequiresExactValue(string? value, bool expected) => Assert.Equal(expected, AtlasUploadMode.IsRequested(value));

    [Theory]
    [InlineData(null, false, PerformanceMode.Dynamic)]
    [InlineData("1", false, PerformanceMode.Static)]
    [InlineData("2", false, PerformanceMode.Dynamic)]
    [InlineData("2", true, PerformanceMode.Static)]
    [InlineData("3", false, PerformanceMode.Dynamic)]
    [InlineData("3", true, PerformanceMode.Static)]
    [InlineData("4", true, PerformanceMode.Dynamic)]
    public void PartialModesRequireReadyBeforeChangingNativeBackend(string? requested, bool ready, PerformanceMode expected) =>
        Assert.Equal(expected, AtlasUploadMode.SelectRequest(requested, ready, PerformanceMode.Dynamic));

    [Fact]
    public void ConstructorPinPreservesWholeInstalledBodyAndRejectsChanges()
    {
        var original = PatchProcessor.GetOriginalInstructions(AtlasUploadMode.Target);
        var patched = AtlasUploadMode.Transpiler(original).ToList();
        Assert.Equal(original.Select(i => i.ToString()), patched.Select(i => i.ToString()));
        original.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => AtlasUploadMode.Transpiler(original).ToList());
        Assert.False(AtlasUploadMode.Target.ContainsGenericParameters);
    }

    [Fact]
    public void BackendBinaryPinMatchesInstalledCodeAndRejectsOtherBytes()
    {
        AtlasUploadMode.RequireBackendBinary(Path.Combine(GameAssemblies.GameBin, "HalflingPlatformWDX.dll"));
        string path = Path.Combine(Path.GetTempPath(), "performance-atlas-backend-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            File.WriteAllBytes(path, [0, 1, 2, 3]);
            Assert.Throws<InvalidOperationException>(() => AtlasUploadMode.RequireBackendBinary(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(PerformanceMode.Dynamic)]
    [InlineData(PerformanceMode.Static)]
    [InlineData(PerformanceMode.Immutable)]
    [InlineData(PerformanceMode.DynamicStreaming)]
    [InlineData((PerformanceMode)(-1))]
    public void RealConstructorHookPreservesNativeNullShipException(PerformanceMode mode)
    {
        ConstructorInfo target = AtlasUploadMode.Target;
        var baseline = Assert.Throws<TargetInvocationException>(() => target.Invoke([null, mode]));
        var original = Assert.IsType<ArgumentNullException>(baseline.InnerException);
        var harmony = new Harmony(TestHarmonyId);
        try
        {
            harmony.Patch(target,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(AtlasUploadMode), nameof(AtlasUploadMode.Prefix))) { priority = Priority.First },
                transpiler: new HarmonyMethod(AccessTools.Method(typeof(AtlasUploadMode), nameof(AtlasUploadMode.Transpiler))));
            harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(AtlasUploadModeTests), nameof(ObserveMode))) { priority = Priority.Last });
            var patched = Assert.Throws<TargetInvocationException>(() => target.Invoke([null, mode]));
            var actual = Assert.IsType<ArgumentNullException>(patched.InnerException);
            Assert.Equal(original.ParamName, actual.ParamName);
            Assert.Equal(original.Message, actual.Message);
            Assert.Equal(AtlasUploadMode.Select(AtlasUploadMode.Enabled, mode), _observedMode);
        }
        finally
        {
            harmony.UnpatchAll(TestHarmonyId);
        }
    }

    private static void ObserveMode(PerformanceMode perfMode) => _observedMode = perfMode;
}
