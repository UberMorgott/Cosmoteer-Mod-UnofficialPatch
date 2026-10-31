using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

public sealed class HarmonyPatchIdentityTests
{
    private static MethodInfo First => typeof(HarmonyPatchIdentityTests).GetMethod(nameof(Hook), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static MethodInfo Second => typeof(HarmonyPatchIdentityTests).GetMethod(nameof(OtherHook), BindingFlags.Static | BindingFlags.NonPublic)!;
    [Fact]
    public void NullDescriptorsShareOnlyValidatedModuleAndTokenCacheWithoutRecordWrites()
    {
        var resolver = new HarmonyPatchIdentity.Resolver(HarmonyPatchIdentity.ModuleMap([First.Module]));
        var first = Record(First); Set(first, "patchMethod", null);
        Assert.Same(First, resolver.Read(first)); Assert.Equal(1, resolver.Resolutions);
        var fresh = Record(First); Set(fresh, "patchMethod", null);
        Assert.Same(First, resolver.Read(fresh)); Assert.Equal(1, resolver.Resolutions);
        Assert.Null(AccessTools.Field(typeof(Patch), "patchMethod").GetValue(fresh));
        fresh.PatchMethod = Second;
        Assert.Same(Second, resolver.Read(fresh)); // A fresh non-null raw method is authoritative.
    }
    [Theory]
    [InlineData("token")]
    [InlineData("guid")]
    [InlineData("raw")]
    [InlineData("generic")]
    public void ChangedOrUnsupportedDescriptorIsNotHiddenByWarmCache(string change)
    {
        var resolver = new HarmonyPatchIdentity.Resolver(HarmonyPatchIdentity.ModuleMap([First.Module]));
        var patch = Record(First); Assert.Same(First, resolver.Read(patch));
        switch (change)
        {
            case "token": Set(patch, "methodToken", Second.MetadataToken); break;
            case "guid": Set(patch, "moduleGUID", Guid.NewGuid().ToString()); break;
            case "raw": Set(patch, "patchMethod", Second); break;
            case "generic": patch.PatchMethod = typeof(HarmonyPatchIdentityTests).GetMethod(nameof(GenericHook), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(int)); break;
        }
        Assert.Throws<NotSupportedException>(() => resolver.Read(patch));
    }
    [Fact]
    public void SameMvidFromAnotherActualModuleIsAmbiguous()
    {
        WeakReference context = RejectAlias();
        for (int i = 0; i < 10 && context.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(context.IsAlive);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RejectAlias()
    {
        var context = new AssemblyLoadContext("patch-identity-copy", isCollectible: true);
        var weak = new WeakReference(context);
        try
        {
            var other = context.LoadFromAssemblyPath(typeof(Patch).Assembly.Location);
            Assert.Equal(typeof(Patch).Module.ModuleVersionId, other.ManifestModule.ModuleVersionId);
            Assert.Throws<NotSupportedException>(() => HarmonyPatchIdentity.ModuleMap([typeof(Patch).Module, other.ManifestModule]));
        }
        finally { context.Unload(); }
        return weak;
    }
    [Fact]
    public void InstalledLayoutAndHashArePinned()
    {
        HarmonyPatchIdentity.ValidateRuntime();
        Assert.Throws<NotSupportedException>(() => HarmonyPatchIdentity.ValidateRuntime("WRONG"));
    }
    [Fact]
    public void FreshRuntimeViewHonorsNewRemovedAndMetadataChangedRecords()
    {
        var harmony = new Harmony("Performance.Tests.Identity.Fresh");
        MethodInfo target = Second;
        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(First) { priority = 123, before = ["other-owner"], debug = false });
            var view = HarmonyPatchIdentity.ReadRuntimeView();
            var registration = Assert.Single(view.Registrations, r => r.Target == target && r.Owner == harmony.Id);
            Assert.Equal(123, registration.Priority); Assert.Equal(new[] { "other-owner" }, registration.Before);
            harmony.UnpatchAll(harmony.Id);
            Assert.DoesNotContain(HarmonyPatchIdentity.ReadRuntimeView().Registrations, r => r.Target == target && r.Owner == harmony.Id);
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
    [Fact]
    public void PatchedResolutionDependencyRejectsEvenWarmCache()
    {
        var resolver = new HarmonyPatchIdentity.Resolver(HarmonyPatchIdentity.ModuleMap([First.Module]));
        Assert.Same(First, resolver.Read(Record(First)));
        var harmony = new Harmony("Performance.Tests.Identity.Resolution");
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(Patch), "PatchMethod"), prefix: new HarmonyMethod(First));
            Assert.Throws<NotSupportedException>(() => HarmonyPatchIdentity.ReadRuntimeView());
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
    private static Patch Record(MethodInfo method) => new(method, 0, "test", 400, [], [], false);
    private static void Set(Patch patch, string field, object? value) => AccessTools.Field(typeof(Patch), field).SetValue(patch, value);
    private static void Hook() { }
    private static void OtherHook() { }
    private static void GenericHook<T>() => GC.KeepAlive(typeof(T));
}
