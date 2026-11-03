using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Blueprints;
using Halfling.Collections;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Every ship split copies the whole part metadata of the old ship into each new piece and then culls what the piece does
/// not own (<c>ShipMetadata.CullPartMetadata</c>). Each cull goes through <c>Algorithms.RemoveAll</c>, which collects the
/// matching entries into a new List (grown by doubling) and returns it; every caller discards it (about 20 MB of garbage
/// in a 40-ship battle, most of it in <c>PartStateDictionary.RemoveAll</c> for the toggle states).
/// <para/>
/// Those calls (in CullPartMetadata and in the toggle-state PartStateDictionary.RemoveAll, both followed by <c>pop</c>) go
/// to <see cref="RemoveAll{T}"/>: the same non-IList path (predicate per item in enumeration order, then Remove per
/// collected item in that order) with one reused List per item type. IList/array, read-only and null arguments take the
/// vanilla call, so its own path and exceptions are unchanged. Off: <c>PERFORMANCE_SPLIT_ALLOC=0</c>.
/// <para/>
/// Not done: pre-sizing the destination collections of CopyPartMetadataFrom. The two biggest (toggle states and colors)
/// live inside PartStateDictionary, which BlueprintToggleUpdates' scan forbids touching; the rest would save about
/// 6 MB per 42 s battle (half the ReversibleMultiDictionary resize bytes), under 1% of all allocations.
/// </summary>
internal static class SplitAlloc
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_SPLIT_ALLOC"));
    internal static bool IsRequested(string? value) => value != "0";

    internal static readonly MethodInfo Cull = AccessTools.Method(typeof(ShipMetadata), nameof(ShipMetadata.CullPartMetadata))
        ?? throw new MissingMethodException(nameof(ShipMetadata), nameof(ShipMetadata.CullPartMetadata));

    /// <summary>Shared generic code (TKey is a struct over a reference type); the color instantiation has no RemoveAll caller.</summary>
    internal static readonly MethodInfo ToggleRemoveAll =
        AccessTools.Method(typeof(PartStateDictionary<(PartSaveData, ID<PartToggleGuiRules>), int>), "RemoveAll")
        ?? throw new MissingMethodException("PartStateDictionary", "RemoveAll");

    private static readonly (MethodInfo Target, string IlSha256, int Sites)[] Targets =
    [
        (Cull, "49B24667D3CA8EE136F5FE7E092FE07D5A45BA82942894DF4407F1CE07EEC7F5", 9),
        (ToggleRemoveAll, "3707CD33CBC6587655FB6D69F446C35E5C67F58C72C35452A5E09B2D529538AC", 2),
    ];

    internal static readonly MethodInfo VanillaRemoveAll = typeof(Algorithms).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(Algorithms.RemoveAll) && m.IsGenericMethodDefinition && m.GetParameters() is [var c, var p]
            && c.ParameterType.IsGenericType && c.ParameterType.GetGenericTypeDefinition() == typeof(ICollection<>)
            && p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(Predicate<>));

    private static readonly MethodInfo Replacement = AccessTools.Method(typeof(SplitAlloc), nameof(RemoveAll));

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? Targets.Select(t => ((MethodBase)t.Target, typeof(SplitAlloc))).ToArray() : [];

    /// <summary>Each discarded <c>Algorithms.RemoveAll&lt;T&gt;(c, p); pop</c> becomes <c>SplitAlloc.RemoveAll&lt;T&gt;(c, p)</c>.</summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        (MethodInfo target, string sha, int expected) = Targets.Single(t => t.Target.Name == original.Name);
        string name = target.DeclaringType!.Name + "." + target.Name;
        List<CodeInstruction> code = Patcher.RequireIl(instructions, sha, name);
        int sites = 0;
        for (int i = 0; i < code.Count; i++)
        {
            if (code[i].operand is not MethodInfo { IsGenericMethod: true } m || m.GetGenericMethodDefinition() != VanillaRemoveAll) continue;
            if (i + 1 >= code.Count || code[i + 1].opcode != OpCodes.Pop || code[i + 1].labels.Count != 0 || code[i + 1].blocks.Count != 0)
                throw new InvalidOperationException($"{name}: RemoveAll result is used");
            code[i].opcode = OpCodes.Call;
            code[i].operand = Replacement.MakeGenericMethod(m.GetGenericArguments());
            code[i + 1].opcode = OpCodes.Nop;
            sites++;
        }
        if (sites != expected) throw new InvalidOperationException($"{name}: {sites} RemoveAll calls, expected {expected}");
        return code;
    }

    private static class Pool<T>
    {
        internal static List<T>? Free;
    }

    /// <summary>
    /// <c>_ = Algorithms.RemoveAll(collection, predicate)</c> without the result list: same predicate and Remove calls in the
    /// same order. The list is reused per T (taken atomically, so nested or concurrent calls get their own); after an
    /// exception it is simply not returned.
    /// </summary>
    public static void RemoveAll<T>(ICollection<T>? collection, Predicate<T>? predicate)
    {
        if (collection == null || predicate == null || collection is IList<T> || collection.IsReadOnly)
        {
            _ = Algorithms.RemoveAll(collection!, predicate!);
            return;
        }
        List<T> removed = Interlocked.Exchange(ref Pool<T>.Free, null) ?? [];
        foreach (T item in collection)
        {
            if (predicate(item)) removed.Add(item);
        }
        foreach (T item in removed) collection.Remove(item);
        removed.Clear();
        Volatile.Write(ref Pool<T>.Free, removed);
    }
}
