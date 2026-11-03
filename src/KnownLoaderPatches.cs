using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using Cosmoteer.Ships.Parts.Weapons;
using Halfling.Physics2D.Dynamics;
using HarmonyLib;

namespace UnofficialPatch;

internal static class KnownLoaderPatches
{
    private const string LoaderOwner = "Cosmoteer.ModLoader";
    private const string LoaderHash = "AD81F64FD410062711BFCC193FF45E56B1DCD087E13DE851AB88C47895CC1592";
    private static readonly Guid LoaderMvid = new("a58d1d86-b6b1-4f82-856b-049a81d2cd7c");
    private static readonly ConditionalWeakTable<Module, Lazy<Proof>> Proofs = new();
    internal enum Kind { Prefix, Postfix, Transpiler, Finalizer, InnerPrefix, InnerPostfix }
    internal readonly record struct Registration(MethodBase Target, string Owner, Kind HookKind, MethodInfo Hook,
        int Index = 0, int Priority = 400, string[]? Before = null, string[]? After = null, bool Debug = false, InnerMethod? InnerMethod = null);
    private readonly record struct Spec(int Module, int Target, Kind Kind, int Patch, string NativeHash, string PatchHash);
    private static readonly Spec[] Specs =
    [
        new(0, 0x060045AA, Kind.Transpiler, 0x0600000B, "9A0F9B2F6C4B0F1F121D8675D353A49908888956F8260A253CBC776B5BE7FCB0", "0E3F6E0C3AE596366093068ABF9CE2EAF368A6C05DD0817A94DFF1184FDAFE4D"),
        new(0, 0x06003865, Kind.Postfix, 0x0600000C, "8B3818AAA6CAC58AC9FE6A57CE66603F5C76B9A0536AEBFD451D495DB8FA8FC6", "4D3FE31D2496EA1371D77E5FEA214854BA59400AE283F8D98D35703DECFDDF6C"),
        new(0, 0x06003866, Kind.Prefix, 0x0600000D, "27F73BDAEAFCC958EDE5BC0A19BBCB8E7B6345141C7404C08E747F82BCDD2FB6", "310F3DE32A75F75B00672F148B47CCAB3D35006BB1DB646D7B2A46B1889D5FBF"),
        new(0, 0x06003861, Kind.Postfix, 0x0600000E, "5AB6F42A7328F2B0E9314C3D578C6BC8C6AD12D95B5604D41618278CB874FA36", "578B44B879E03B4F8351855A4ED0898D8429FE8CCF92796123EFDB21F7D8A299"),
        new(0, 0x06004516, Kind.Prefix, 0x06000012, "9E349126376EABB7F4E53F5330C87FAB8066FB93B6C2B944B45408EE828FC42B", "8CFE40A4DFC6616EEAD7965E9804DAD9FBBADB92C02159F009E46DDE753ABC2D"),
        new(0, 0x06004516, Kind.Postfix, 0x06000013, "9E349126376EABB7F4E53F5330C87FAB8066FB93B6C2B944B45408EE828FC42B", "4EEE1FC77AD7BDD42BCABA07EC825F0B88FD3C0336312E9AA2C19CA751D15041"),
        new(0, 0x06004525, Kind.Postfix, 0x06000014, "AE39F2ADE788C37A09490052D63C92CD2C0539ED515C8C8CBE61FA54F8C47110", "F3A0B08BFDE29746300632A3E903E42F750712187170E59B78556B2AFEBE6769"),
        new(0, 0x060002EF, Kind.Postfix, 0x06000015, "D0103C1BB946103A5B77E88394641D692E9218580463E69293BFE8A95D4FD0B5", "42F1CB9FD59D7012B8674B2ED252E4838E2C31C5EF6000EE095D958DEACCA461"),
        new(1, 0x06005240, Kind.Prefix, 0x06000016, "ED2DB3FA766D84930C88974BF069E5F70FA380DFD297C651B2FEA034A87E0158", "69B9E6C7540A774E8EF60783C4D007B216891650D2E1F8C2E53BC21F63BC9D36"),
        new(2, 0x0600055E, Kind.Prefix, 0x06000017, "0621B16D803A49CE5CE017701237716EB40555DEC98523C353950B6C16A590D9", "1B787BAFA4A5814864A09477F3314DFDE3167A7F681A599B8A677E4EA494266E"),
    ];
    internal sealed class Proof
    {
        private readonly Registration[] _allowed;
        internal ReadOnlySpan<Registration> Allowed => _allowed;
        private int[]? _installedIndices;
        internal Proof(Registration[] allowed) => _allowed = allowed;
        internal Registration[] Copy() => (Registration[])_allowed.Clone();
        internal bool Allows(Registration registration)
        {
            for (int i = 0; i < _allowed.Length; i++)
                if (Match(_allowed[i], registration) && DefaultMetadata(registration) &&
                    (_installedIndices == null || registration.Index == _installedIndices[i])) return true;
            return false;
        }
        internal void ValidateIndices(int[] indices)
        {
            int[] baseline = Volatile.Read(ref _installedIndices) ?? Interlocked.CompareExchange(ref _installedIndices, (int[])indices.Clone(), null) ?? indices;
            if (!baseline.AsSpan().SequenceEqual(indices)) throw new NotSupportedException("Loader installation ordering changed");
        }
    }
    internal static Registration[] Expected(Module module) => GetProof(module).Copy();
    private static Module[] NativeModules() => [typeof(BeamEmitter).Module, typeof(World).Module, typeof(Environment).Module];

    /// <summary>Native methods the pinned loader patches (resolution only, no hash proof): its owner is foreign to dependency checks.</summary>
    internal static MethodBase[] NativeTargets()
    {
        Module[] native = NativeModules();
        return Specs.Select(spec => native[spec.Module].ResolveMethod(spec.Target) ?? throw new NotSupportedException("Loader target missing")).ToArray();
    }
    private static Proof GetProof(Module module) => Proofs.GetValue(module, key => new Lazy<Proof>(() => ReadProof(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    private static Proof ReadProof(Module module)
    {
        if (!IsLoader(module) || module.ModuleVersionId != LoaderMvid || !ReferenceEquals(module, module.Assembly.ManifestModule))
            throw new NotSupportedException("Unsupported loader module instance/MVID");
        string location = module.Assembly.Location;
        if (string.IsNullOrEmpty(location)) throw new NotSupportedException("Loader SHA256 requires the actual file-backed assembly instance");
        using (FileStream file = File.OpenRead(location))
            if (Convert.ToHexString(SHA256.HashData(file)) != LoaderHash) throw new NotSupportedException("Loader SHA256 mismatch");
        Module[] native = NativeModules();
        if (native[0].ModuleVersionId != new Guid("fd7c0bba-64d3-40b4-a571-bf8b34eb4277") ||
            native[1].ModuleVersionId != new Guid("29d04acb-ae49-4f3b-8499-f03c9144a3a9") ||
            (native[2].ModuleVersionId != new Guid("77d2994f-32f8-4ff2-8313-7ebf44feba98") && native[2].ModuleVersionId != new Guid("47f87616-cc44-40db-91db-a39140060003")))
            throw new NotSupportedException("Unsupported native loader target module profile");
        string[] nativeFiles =
        [
            "F9451FBED4A11396C816D3F44EBDD836E48BCE68BA6AEAE28C3E534E037207D4",
            "80213C7C8182CE63B9075A48DFD80A7614B1FA2AB3BCD16A69D5363CF9A4B391",
            native[2].ModuleVersionId == new Guid("77d2994f-32f8-4ff2-8313-7ebf44feba98")
                ? "1125ACC8106C43FC8BAD2D203C4C4485DF6182D292846C2FFF415C1040C54678"
                : "69D56E12535D3A8F2F8F89AC8F97577E6DC788D26F2E7B69BE8FB5F6FF17FF28",
        ];
        for (int i = 0; i < native.Length; i++)
        {
            using FileStream file = File.OpenRead(native[i].Assembly.Location);
            if (Convert.ToHexString(SHA256.HashData(file)) != nativeFiles[i]) throw new NotSupportedException("Native loader target SHA256 mismatch: " + native[i].Name);
        }
        var allowed = new Registration[Specs.Length];
        for (int i = 0; i < Specs.Length; i++)
        {
            Spec spec = Specs[i];
            MethodBase target = native[spec.Module].ResolveMethod(spec.Target) ?? throw new NotSupportedException("Loader target missing");
            MethodInfo hook = module.ResolveMethod(spec.Patch) as MethodInfo ?? throw new NotSupportedException("Loader patch missing");
            if (!hook.IsStatic || hook.DeclaringType?.FullName != "ModLoader.ModLoader" ||
                Hash(target) != spec.NativeHash || Hash(hook) != spec.PatchHash)
                throw new NotSupportedException("Pinned loader target/patch IL changed: " + target.DeclaringType?.FullName + "." + target.Name);
            allowed[i] = new(target, LoaderOwner, spec.Kind, hook);
        }
        return new Proof(allowed);
    }
    private static string Hash(MethodBase method) => method.GetMethodBody()?.GetILAsByteArray() is { } il
        ? Convert.ToHexString(SHA256.HashData(il)) : throw new NotSupportedException("Loader method body unavailable");
    private static bool IsLoader(Module module) => module.Assembly.GetName().Name == "ModLoader";
    private static bool Same(MethodBase a, MethodBase b) => ReferenceEquals(a.Module, b.Module) && a.Equals(b);
    private static bool Match(Registration a, Registration b) => a.Owner == b.Owner && a.HookKind == b.HookKind && Same(a.Target, b.Target) && Same(a.Hook, b.Hook);
    private static bool DefaultMetadata(Registration registration) => registration.Index >= 0 && registration.Priority == 400 &&
        (registration.Before == null || registration.Before.Length == 0) && (registration.After == null || registration.After.Length == 0) &&
        !registration.Debug && registration.InnerMethod == null;
    internal static string Describe(Registration patch)
    {
        static string Bounded(string text, int limit) => text.Length <= limit ? text : text[..limit];
        return Bounded(patch.Target.DeclaringType?.FullName ?? "<unknown>", 128) + "." + Bounded(patch.Target.Name, 80) +
            " owner=" + Bounded(patch.Owner, 80) + " kind=" + patch.HookKind;
    }
    internal static Proof Validate(IEnumerable<Registration> registrations, Module[] loaderModules)
    {
        Registration[] all = registrations as Registration[] ?? registrations.ToArray();
        foreach (Registration registration in all)
            if (IsLoader(registration.Target.Module))
                throw new NotSupportedException("Harmony patch on loader module: " + Describe(registration));
        if (loaderModules.Length == 0)
        {
            foreach (Registration registration in all)
                if (registration.Owner == LoaderOwner || IsLoader(registration.Hook.Module))
                    throw new NotSupportedException("Unknown loader assembly instance: " + Describe(registration));
            return new Proof([]);
        }
        if (loaderModules.Length != 1) throw new NotSupportedException("Multiple loader assembly instances are unsupported");
        Module module = loaderModules[0];
        Proof proof = GetProof(module);
        ReadOnlySpan<Registration> expected = proof.Allowed;
        var seen = new bool[expected.Length];
        var indices = new int[expected.Length];
        foreach (Registration registration in all)
        {
            if (registration.Owner != LoaderOwner && !IsLoader(registration.Hook.Module)) continue;
            int index = -1;
            for (int i = 0; i < expected.Length; i++) if (Match(expected[i], registration)) { index = i; break; }
            if (index < 0) throw new NotSupportedException("Unexpected loader patch/module instance: " + Describe(registration));
            if (!DefaultMetadata(registration)) throw new NotSupportedException("Loader descriptor metadata changed: " + Describe(registration));
            if (seen[index]) throw new NotSupportedException("Duplicate loader patch: " + Describe(registration));
            seen[index] = true;
            indices[index] = registration.Index;
        }
        int missing = Array.IndexOf(seen, false);
        if (missing >= 0) throw new NotSupportedException("Incomplete loader patch inventory: " + Describe(expected[missing]));
        proof.ValidateIndices(indices);
        return proof;
    }
    internal static Registration[] RuntimeRegistrations()
    {
        return HarmonyPatchIdentity.ReadRuntimeView().Registrations;
    }
    internal static Module[] RuntimeLoaderModules() => AppDomain.CurrentDomain.GetAssemblies()
        .Where(assembly => assembly.GetName().Name == "ModLoader").Select(assembly => assembly.ManifestModule).ToArray();
}
