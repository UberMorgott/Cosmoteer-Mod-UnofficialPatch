using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Threading;
using Xunit;

namespace UnofficialPatch.Tests;

public sealed class KnownLoaderPatchesTests
{
    private const string LoaderPath = @"D:\Steam\steamapps\workshop\content\799600\3577650065\ModLoader.dll";
    private static Assembly Loader() => AssemblyLoadContext.Default.LoadFromAssemblyPath(LoaderPath);

    [Fact]
    public void AbsentLoaderAndRegistryAreAccepted()
    {
        var proof = KnownLoaderPatches.Validate([], []);
        Assert.False(proof.Allows(new(typeof(KnownLoaderPatchesTests).GetMethod(nameof(AbsentLoaderAndRegistryAreAccepted))!, "Cosmoteer.ModLoader", KnownLoaderPatches.Kind.Prefix, Patch())));
    }

    [Fact]
    public void CompleteTenTuplesAreAcceptedWithoutExecutingLoaderCode()
    {
        Module module = Loader().ManifestModule;
        var expected = KnownLoaderPatches.Expected(module);
        Assert.Equal(10, expected.Length); Assert.Equal(9, expected.Select(p => p.Target).Distinct().Count());
        var proof = KnownLoaderPatches.Validate(expected, [module]);
        Assert.All(expected, p => Assert.True(proof.Allows(p)));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("empty-present")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("owner")]
    [InlineData("kind")]
    [InlineData("method")]
    [InlineData("helper-detour")]
    [InlineData("generated-detour")]
    [InlineData("priority")]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("debug")]
    [InlineData("index")]
    public void EveryUnprovenInventoryIsRejected(string change)
    {
        Module module = Loader().ManifestModule;
        var expected = KnownLoaderPatches.Expected(module);
        KnownLoaderPatches.Validate(expected, [module]);
        var altered = expected.ToList();
        switch (change)
        {
            case "partial": altered.RemoveAt(0); break;
            case "empty-present": altered.Clear(); break;
            case "extra": altered.Add(expected[0] with { Target = typeof(KnownLoaderPatchesTests).GetMethod(nameof(AbsentLoaderAndRegistryAreAccepted))! }); break;
            case "duplicate": altered.Add(expected[0]); break;
            case "owner": altered[0] = altered[0] with { Owner = "spoofed-owner" }; break;
            case "kind": altered[0] = altered[0] with { HookKind = KnownLoaderPatches.Kind.Prefix }; break;
            case "method": altered[0] = altered[0] with { Hook = Patch() }; break;
            case "priority": altered[0] = altered[0] with { Priority = 401 }; break;
            case "before": altered[0] = altered[0] with { Before = ["changed"] }; break;
            case "after": altered[0] = altered[0] with { After = ["changed"] }; break;
            case "debug": altered[0] = altered[0] with { Debug = true }; break;
            case "index": altered[0] = altered[0] with { Index = 1 }; break;
            case "helper-detour": altered.Add(new(module.ResolveMethod(0x0600000A)!, "arbitrary-owner", KnownLoaderPatches.Kind.Prefix, Patch())); break;
            case "generated-detour":
                MethodInfo generated = module.Assembly.GetTypes().Where(t => t.DeclaringType != null)
                    .SelectMany(t => t.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    .First(m => m.Name.Contains('<'));
                altered.Add(new(generated, "morgott.unofficialpatch", KnownLoaderPatches.Kind.Postfix, Patch())); break;
        }
        var error = Assert.Throws<NotSupportedException>(() => KnownLoaderPatches.Validate(altered, [module]));
        Assert.NotEmpty(error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameMvidDoesNotTransferTrustToAnotherAssemblyInstanceOrChangedFile(bool changeHash)
    {
        Module original = Loader().ManifestModule;
        var expected = KnownLoaderPatches.Expected(original);
        string directory = Path.Combine(Path.GetTempPath(), "UnofficialPatch-loader-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string copy = Path.Combine(directory, "ModLoader.dll");
        Exception? proofFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            File.Copy(LoaderPath, copy);
            if (changeHash)
            {
                using FileStream stream = File.Open(copy, FileMode.Append, FileAccess.Write);
                stream.WriteByte(0);
            }
            WeakReference unloaded = RejectCollectibleCopy(copy, original, expected, changeHash);
            for (int i = 0; i < 10 && unloaded.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Assert.False(unloaded.IsAlive);
        }
        catch (Exception error) { proofFailure = error; }
        finally
        {
            try { DeleteCollectibleCopy(copy); Directory.Delete(directory); }
            catch (Exception cleanup) { cleanupFailure = cleanup; }
        }
        if (proofFailure != null && cleanupFailure != null)
            throw new AggregateException("Loader proof and cleanup failed", proofFailure, cleanupFailure);
        if (proofFailure != null) ExceptionDispatchInfo.Capture(proofFailure).Throw();
        if (cleanupFailure != null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }
    private static void DeleteCollectibleCopy(string path)
    {
        // Windows PE mapping release may lag the collectible ALC's weak-reference transition.
        // Retry only this test-owned file; a permanent failure still throws and never hides the proof assertion.
        for (int attempt = 0; ; attempt++)
        {
            try { File.Delete(path); return; }
            catch (Exception error) when (attempt < 9 && error is IOException or UnauthorizedAccessException)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10);
            }
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RejectCollectibleCopy(string path, Module original, KnownLoaderPatches.Registration[] expected, bool changedHash)
    {
        var context = new AssemblyLoadContext("loader-proof-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        context.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
        var weak = new WeakReference(context);
        try
        {
            Assembly other;
            if (changedHash)
            {
                other = context.LoadFromAssemblyPath(path);
            }
            else
            {
                using FileStream stream = File.OpenRead(path);
                other = context.LoadFromStream(stream);
            }
            Assert.Equal(original.ModuleVersionId, other.ManifestModule.ModuleVersionId);
            Assert.NotSame(original, other.ManifestModule);
            var error = Assert.Throws<NotSupportedException>(() => KnownLoaderPatches.Validate(expected, [other.ManifestModule]));
            Assert.Contains(changedHash ? "SHA256" : "instance", error.Message);
        }
        finally { context.Unload(); }
        return weak;
    }
    private static MethodInfo Patch() => typeof(KnownLoaderPatchesTests).GetMethod(nameof(Noop), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static void Noop() { }
}
