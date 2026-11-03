using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Data;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Paint;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>
/// DecalsManager.CopyDecalsFrom (onlyNeeded, called per decal layer from StructureContiguityManager.SplitIntoNewShips) looks
/// up every source decal in <c>ShipRoofRules.RoofDecals</c>, a SortedDictionary whose default comparer is
/// <c>ID&lt;Decal&gt;.CompareTo</c>: <c>string.CompareOrdinal</c> of both names, each fetched under a global lock (~0.5 ms
/// per split tick). Only that one indexer call is replaced by <see cref="Get"/>: a hash lookup in an immutable snapshot of
/// the same dictionary (ID equality = same index), cached per dictionary instance (a readonly field of the rules object, so
/// one per rules instance: new rules, new cache) in a ConditionalWeakTable and rebuilt whenever the dictionary's private
/// version changes (Add, Remove, Clear and the indexer setter all bump it).
/// <para/>
/// Same result: CompareTo returns 0 exactly when the indices are equal (one index per case-insensitive name, so distinct
/// indices have ordinally distinct names), so a key is in the tree iff its index is in the snapshot, with the same value.
/// A miss, a null dictionary, a non-default comparer or a snapshot that could not be built call the native indexer, so the
/// exception (KeyNotFoundException, or GetName's own for an unregistered index) is the native one. Iteration over the
/// source decals is untouched. ID.CompareTo is not changed. Off: <c>UNOFFICIAL_PATCH_DECAL_LOOKUP=0</c>.
/// </summary>
internal static class DecalLookup
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("UNOFFICIAL_PATCH_DECAL_LOOKUP"));
    internal static bool IsRequested(string? value) => value != "0";

    private const string IlSha256 = "4AD4A8BEFA281F2F1D84CA29A8608473A51585481A8389649AA6AE2DD551645A";

    internal static readonly MethodInfo Target = AccessTools.Method(typeof(DecalsManager), nameof(DecalsManager.CopyDecalsFrom))
        ?? throw new MissingMethodException(nameof(DecalsManager), nameof(DecalsManager.CopyDecalsFrom));

    internal static readonly MethodInfo NativeGetItem = typeof(SortedDictionary<ID<Decal>, RoofDecalInfo>).GetProperty("Item")!.GetMethod!;

    internal static readonly MethodInfo Replacement = AccessTools.Method(typeof(DecalLookup), nameof(Get));

    // Private runtime fields; when missing (different BCL) the fix stays off instead of rolling the whole mod back.
    private static readonly FieldInfo? SetField = AccessTools.Field(typeof(SortedDictionary<ID<Decal>, RoofDecalInfo>), "_set");
    private static readonly FieldInfo? VersionField = AccessTools.Field(typeof(SortedSet<KeyValuePair<ID<Decal>, RoofDecalInfo>>), "version");
    private static readonly Func<SortedDictionary<ID<Decal>, RoofDecalInfo>, int>? VersionOf = BuildVersionReader();

    internal static bool Available => VersionOf != null;

    internal static (MethodBase Target, Type Patch)[] Patches
    {
        get
        {
            if (!Requested) return [];
            if (Available) return [(Target, typeof(DecalLookup))];
            Log.Info("Decal lookup: SortedDictionary internals not found; fix inactive (vanilla).");
            return [];
        }
    }

    /// <summary>Exactly the vanilla IL; its single RoofDecals indexer call is redirected to <see cref="Get"/>.</summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = Patcher.RequireIl(instructions, IlSha256, "DecalsManager.CopyDecalsFrom");
        List<CodeInstruction> calls = code.Where(ci => (ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && Equals(ci.operand, NativeGetItem)).ToList();
        if (calls.Count != 1) throw new InvalidOperationException($"DecalsManager.CopyDecalsFrom: expected one RoofDecals lookup, found {calls.Count}");
        calls[0].opcode = OpCodes.Call; // same stack: (dictionary, key) -> RoofDecalInfo; labels and blocks kept
        calls[0].operand = Replacement;
        return code;
    }

    private sealed class Snapshot(int version, Dictionary<ID<Decal>, RoofDecalInfo>? map)
    {
        internal readonly int Version = version;

        /// <summary>Null: this version could not be snapshotted, use the native lookup.</summary>
        internal readonly Dictionary<ID<Decal>, RoofDecalInfo>? Map = map;
    }

    private sealed class Holder
    {
        internal volatile Snapshot? Current;
    }

    private static readonly ConditionalWeakTable<SortedDictionary<ID<Decal>, RoofDecalInfo>, Holder> Cache = new();

    /// <summary>Same value or exception as <c>dictionary[key]</c> (null: the native NullReferenceException).</summary>
    public static RoofDecalInfo Get(SortedDictionary<ID<Decal>, RoofDecalInfo>? dictionary, ID<Decal> key)
    {
        if (dictionary == null || VersionOf == null || !ReferenceEquals(dictionary.Comparer, Comparer<ID<Decal>>.Default)) return dictionary![key];
        Holder holder = Cache.GetValue(dictionary, static _ => new Holder());
        int version = VersionOf(dictionary);
        Snapshot? snapshot = holder.Current;
        if (snapshot == null || snapshot.Version != version)
        {
            snapshot = Build(dictionary, version);
            if (snapshot != null) holder.Current = snapshot;
        }
        if (snapshot?.Map != null && snapshot.Map.TryGetValue(key, out RoofDecalInfo value)) return value;
        return dictionary[key];
    }

    /// <summary>Null when the dictionary changed while it was copied (the caller then uses the native lookup).</summary>
    private static Snapshot? Build(SortedDictionary<ID<Decal>, RoofDecalInfo> dictionary, int version)
    {
        var map = new Dictionary<ID<Decal>, RoofDecalInfo>(dictionary.Count);
        try
        {
            foreach (KeyValuePair<ID<Decal>, RoofDecalInfo> pair in dictionary)
            {
                if (!map.TryAdd(pair.Key, pair.Value)) return new Snapshot(version, null);
            }
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        return VersionOf!(dictionary) == version ? new Snapshot(version, map) : null;
    }

    /// <summary>Test seam: number of keys in the current snapshot of <paramref name="dictionary"/>, or -1 without one.</summary>
    internal static int SnapshotCount(SortedDictionary<ID<Decal>, RoofDecalInfo> dictionary) =>
        Cache.TryGetValue(dictionary, out Holder? h) && h.Current?.Map is { } map ? map.Count : -1;

    private static Func<SortedDictionary<ID<Decal>, RoofDecalInfo>, int>? BuildVersionReader()
    {
        if (SetField == null || VersionField == null || VersionField.FieldType != typeof(int) ||
            !typeof(SortedSet<KeyValuePair<ID<Decal>, RoofDecalInfo>>).IsAssignableFrom(SetField.FieldType))
            return null;
        var method = new DynamicMethod("DecalLookup_Version", typeof(int), [typeof(SortedDictionary<ID<Decal>, RoofDecalInfo>)], typeof(DecalLookup).Module, true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, SetField);
        il.Emit(OpCodes.Ldfld, VersionField);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Func<SortedDictionary<ID<Decal>, RoofDecalInfo>, int>>();
    }
}
