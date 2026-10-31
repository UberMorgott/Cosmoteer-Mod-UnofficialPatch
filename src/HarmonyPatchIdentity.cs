using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using HarmonyLib;

namespace Performance;

internal static class HarmonyPatchIdentity
{
    private const string HarmonyHash = "FD77B88724F4104440DF0CF979A851D35EEC75EA3A7E86297D04ABE47C71AFF6";
    private static readonly Guid HarmonyMvid = new("dc2e7251-4b84-4883-90eb-eb05a041522c");
    private static readonly Type RuntimeMethodType = typeof(HarmonyPatchIdentity).GetMethod(nameof(ValidateRuntime), BindingFlags.Static | BindingFlags.NonPublic)!.GetType();
    private static readonly ConditionalWeakTable<Module, Dictionary<int, MethodInfo>> Cache = new();
    private sealed record Readers(Func<Patch, int> Token, Func<Patch, string?> Guid, Func<Patch, MethodInfo?> Raw);
    private static readonly Readers Fields = BindReaders();
    internal static void ValidateRuntime(string? expected = null)
    {
        Assembly assembly = typeof(Patch).Assembly;
        if (assembly.ManifestModule.ModuleVersionId != HarmonyMvid) throw new NotSupportedException("Harmony identity MVID changed");
        using FileStream file = File.OpenRead(assembly.Location);
        if (Convert.ToHexString(SHA256.HashData(file)) != (expected ?? HarmonyHash)) throw new NotSupportedException("Harmony identity SHA256 changed");
    }
    private static Readers BindReaders()
    {
        ValidateRuntime();
        return new(ReadField<int>("methodToken"), ReadField<string?>("moduleGUID"), ReadField<MethodInfo?>("patchMethod"));
    }
    private static Func<Patch, T> ReadField<T>(string name)
    {
        FieldInfo field = typeof(Patch).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new NotSupportedException("Harmony field missing: " + name);
        if (field.FieldType != typeof(T) || field.IsStatic || field.IsInitOnly ||
            field.IsDefined(typeof(NonSerializedAttribute)) != (name == "patchMethod")) throw new NotSupportedException("Harmony field layout changed: " + name);
        var method = new DynamicMethod("ReadHarmony_" + name, typeof(T), [typeof(Patch)], typeof(HarmonyPatchIdentity), true);
        ILGenerator il = method.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Func<Patch, T>>();
    }
    internal static Dictionary<Guid, Module> ModuleMap(IEnumerable<Module> modules)
    {
        var map = new Dictionary<Guid, Module>();
        foreach (Module module in modules)
        {
            Guid id = module.ModuleVersionId;
            if (map.TryGetValue(id, out Module? existing) && !ReferenceEquals(existing, module))
                throw new NotSupportedException("Ambiguous runtime module MVID: " + id);
            map[id] = module;
        }
        return map;
    }
    internal sealed record View(KnownLoaderPatches.Registration[] Registrations, Module[] LoaderModules);
    internal static View ReadRuntimeView()
    {
        var snapshots = new List<(MethodBase Target, Patches Patches)>();
        foreach (MethodBase target in Harmony.GetAllPatchedMethods())
        {
            Patches patches = Harmony.GetPatchInfo(target) ?? throw new NotSupportedException("Harmony registry changed during identity check");
            if (patches.Prefixes.Count + patches.Postfixes.Count + patches.Transpilers.Count + patches.Finalizers.Count + patches.InnerPrefixes.Count + patches.InnerPostfixes.Count == 0) continue;
            if (ResolutionDependency(target)) throw new NotSupportedException("Patched Harmony resolution dependency: " + target);
            snapshots.Add((target, patches));
        }
        var modules = new List<Module>(); var loaders = new List<Module>();
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            modules.AddRange(assembly.GetModules());
            if (assembly.GetName().Name == "ModLoader") loaders.Add(assembly.ManifestModule);
        }
        var resolver = new Resolver(ModuleMap(modules), Cache);
        var all = new List<KnownLoaderPatches.Registration>();
        foreach (var snapshot in snapshots)
        {
            Add(snapshot.Target, snapshot.Patches.Prefixes, KnownLoaderPatches.Kind.Prefix, resolver, all);
            Add(snapshot.Target, snapshot.Patches.Postfixes, KnownLoaderPatches.Kind.Postfix, resolver, all);
            Add(snapshot.Target, snapshot.Patches.Transpilers, KnownLoaderPatches.Kind.Transpiler, resolver, all);
            Add(snapshot.Target, snapshot.Patches.Finalizers, KnownLoaderPatches.Kind.Finalizer, resolver, all);
            Add(snapshot.Target, snapshot.Patches.InnerPrefixes, KnownLoaderPatches.Kind.InnerPrefix, resolver, all);
            Add(snapshot.Target, snapshot.Patches.InnerPostfixes, KnownLoaderPatches.Kind.InnerPostfix, resolver, all);
        }
        return new(all.ToArray(), loaders.ToArray());
    }
    private static bool ResolutionDependency(MethodBase method)
    {
        Module module = method.Module;
        if (ReferenceEquals(module, typeof(Patch).Module) || ReferenceEquals(module, typeof(System.Linq.Enumerable).Module) ||
            ReferenceEquals(module, typeof(Parallel).Module)) return true;
        Type? type = method.DeclaringType;
        return ReferenceEquals(module, typeof(object).Module) && (type == typeof(AppDomain) || type == typeof(Guid) ||
            type?.Namespace?.StartsWith("System.Reflection", StringComparison.Ordinal) == true ||
            (type == typeof(Environment) && method.Name == "get_ProcessorCount"));
    }
    private static void Add(MethodBase target, IEnumerable<Patch> patches, KnownLoaderPatches.Kind kind, Resolver resolver, List<KnownLoaderPatches.Registration> all)
    {
        foreach (Patch patch in patches)
        {
            if (patch.owner == null || patch.before == null || patch.after == null || patch.innerMethod != null || patch.index < 0)
                throw new NotSupportedException("Unsupported Harmony descriptor metadata: " + target);
            all.Add(new(target, patch.owner, kind, resolver.Read(patch), patch.index, patch.priority, patch.before, patch.after, patch.debug, patch.innerMethod));
        }
    }
    internal sealed class Resolver(Dictionary<Guid, Module> modules, ConditionalWeakTable<Module, Dictionary<int, MethodInfo>>? cache = null)
    {
        private readonly ConditionalWeakTable<Module, Dictionary<int, MethodInfo>> _cache = cache ?? new();
        internal int Resolutions { get; private set; }
        internal MethodInfo Read(Patch patch)
        {
            int token = Fields.Token(patch); string? text = Fields.Guid(patch);
            if (!Guid.TryParse(text, out Guid id) || !modules.TryGetValue(id, out Module? module)) throw new NotSupportedException("Harmony descriptor module missing/ambiguous");
            MethodInfo? raw = Fields.Raw(patch);
            if (raw != null) { ValidateMethod(raw, module, token); return raw; }
            Dictionary<int, MethodInfo> methods = _cache.GetOrCreateValue(module);
            if (methods.TryGetValue(token, out MethodInfo? known)) { ValidateMethod(known, module, token); return known; }
            MethodInfo resolved = patch.PatchMethod ?? throw new NotSupportedException("Harmony descriptor method missing");
            Resolutions++; ValidateMethod(resolved, module, token); methods.Add(token, resolved); return resolved;
        }
        private static void ValidateMethod(MethodInfo method, Module module, int token)
        {
            if (!ReferenceEquals(method.Module, module) || method.MetadataToken != token || method.IsGenericMethod ||
                method.DeclaringType == null || method.DeclaringType.IsGenericType || method.GetType() != RuntimeMethodType)
                throw new NotSupportedException("Inconsistent/unsupported Harmony method descriptor");
        }
    }
}
