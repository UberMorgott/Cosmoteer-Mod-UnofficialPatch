using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Xunit;

namespace Performance.Tests;

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
    public void EveryUnprovenInventoryIsRejected(string change)
    {
        Module module = Loader().ManifestModule;
        var expected = KnownLoaderPatches.Expected(module);
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
            case "helper-detour": altered.Add(new(module.ResolveMethod(0x0600000A)!, "arbitrary-owner", KnownLoaderPatches.Kind.Prefix, Patch())); break;
            case "generated-detour":
                MethodInfo generated = module.Assembly.GetTypes().Where(t => t.DeclaringType != null)
                    .SelectMany(t => t.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    .First(m => m.Name.Contains('<'));
                altered.Add(new(generated, "morgott.performance", KnownLoaderPatches.Kind.Postfix, Patch())); break;
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
        string directory = Path.Combine(Path.GetTempPath(), "Performance-loader-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string copy = Path.Combine(directory, "ModLoader.dll");
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
        finally { File.Delete(copy); Directory.Delete(directory); }
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
