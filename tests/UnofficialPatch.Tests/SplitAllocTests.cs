using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Halfling.Collections;
using HarmonyLib;
using Xunit;

namespace UnofficialPatch.Tests;

public sealed class SplitAllocTests
{
    [Fact]
    public void KillSwitchIsOnlyZero()
    {
        Assert.False(SplitAlloc.IsRequested("0"));
        Assert.True(SplitAlloc.IsRequested(null));
        Assert.True(SplitAlloc.IsRequested("1"));
        Assert.Equal(2, SplitAlloc.Patches.Length);
    }

    /// <summary>Against the real game IL: only the discarded RemoveAll calls (and their pops) change.</summary>
    [Fact]
    public void TranspilersMatchRealGameIl()
    {
        foreach ((MethodBase target, _) in SplitAlloc.Patches)
        {
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(target);
            List<CodeInstruction> code = SplitAlloc.Transpiler(PatchProcessor.GetOriginalInstructions(target), target).ToList();
            Assert.Equal(original.Count, code.Count);
            int calls = 0;
            for (int i = 0; i < code.Count; i++)
            {
                Assert.Equal(original[i].labels.Count, code[i].labels.Count);
                if (code[i].opcode == original[i].opcode && code[i].operand?.ToString() == original[i].operand?.ToString()) continue;
                if (original[i].opcode == OpCodes.Pop)
                {
                    Assert.Equal(OpCodes.Nop, code[i].opcode);
                    continue;
                }
                var vanilla = Assert.IsAssignableFrom<MethodInfo>(original[i].operand);
                var ours = Assert.IsAssignableFrom<MethodInfo>(code[i].operand);
                Assert.Equal(SplitAlloc.VanillaRemoveAll, vanilla.GetGenericMethodDefinition());
                Assert.Equal(nameof(SplitAlloc.RemoveAll), ours.Name);
                Assert.Equal(typeof(SplitAlloc), ours.DeclaringType);
                Assert.Equal(vanilla.GetGenericArguments(), ours.GetGenericArguments());
                Assert.Equal(OpCodes.Pop, original[i + 1].opcode);
                calls++;
            }
            Assert.Equal(target == SplitAlloc.Cull ? 9 : 2, calls);
        }
        Assert.False(SharedGenericRetarget.IsShared(SplitAlloc.Cull));
        Assert.True(SharedGenericRetarget.IsShared(SplitAlloc.ToggleRemoveAll));
    }

    [Fact]
    public void TranspilerRejectsChangedIl()
    {
        List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(SplitAlloc.Cull);
        code.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => SplitAlloc.Transpiler(code, SplitAlloc.Cull).ToList());
    }

    /// <summary>Records every Remove (and forwards it), so the Remove order is compared too.</summary>
    private sealed class Logged<T>(ICollection<T> inner, List<string> log) : ICollection<T>
    {
        public int Count => inner.Count;
        public bool IsReadOnly => inner.IsReadOnly;
        public void Add(T item) => inner.Add(item);
        public void Clear() => inner.Clear();
        public bool Contains(T item) => inner.Contains(item);
        public void CopyTo(T[] array, int arrayIndex) => inner.CopyTo(array, arrayIndex);
        public IEnumerator<T> GetEnumerator() => inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public bool Remove(T item)
        {
            log.Add("remove " + item);
            return inner.Remove(item);
        }
    }

    /// <summary>Same dictionary history (adds and removes, so a free list) in both; returns it.</summary>
    private static Dictionary<int, int> Build(int seed)
    {
        var rng = new Random(seed);
        var dict = new Dictionary<int, int>();
        for (int step = 0; step < 400; step++)
        {
            int key = rng.Next(300);
            if (rng.Next(3) == 0) dict.Remove(key);
            else dict[key] = rng.Next(1000);
        }
        return dict;
    }

    private static ReversibleMultiDictionary<int, int> BuildMulti(int seed)
    {
        var rng = new Random(seed);
        var dict = new ReversibleMultiDictionary<int, int>();
        for (int step = 0; step < 400; step++)
        {
            int key = rng.Next(60), value = rng.Next(60);
            if (rng.Next(3) == 0) dict.Remove(key, value);
            else if (!dict.Contains(key, value)) dict.Add(key, value);
        }
        return dict;
    }

    private static List<string> Run<T>(ICollection<T> collection, Func<T, bool> keep, bool pooled)
    {
        var log = new List<string>();
        var logged = new Logged<T>(collection, log);
        Predicate<T> predicate = item =>
        {
            log.Add("test " + item);
            return !keep(item);
        };
        if (pooled) SplitAlloc.RemoveAll(logged, predicate);
        else log.Add("result " + string.Join(",", Algorithms.RemoveAll(logged, predicate)));
        return log;
    }

    /// <summary>Pooled vs vanilla: predicate and Remove calls in the same order, same contents, same order after later adds.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SameCallsAndOrderAsVanilla(int seed)
    {
        for (int round = 0; round < 3; round++) // later rounds reuse the pooled list
        {
            Dictionary<int, int> vanilla = Build(seed + round), pooled = Build(seed + round);
            Func<KeyValuePair<int, int>, bool> keep = kvp => (kvp.Key * 7 + kvp.Value) % 3 != 0;
            List<string> vanillaLog = Run(vanilla, keep, pooled: false);
            List<string> pooledLog = Run(pooled, keep, pooled: true);
            Assert.Contains(pooledLog, l => l.StartsWith("remove", StringComparison.Ordinal));
            Assert.Equal(vanillaLog.Where(l => !l.StartsWith("result", StringComparison.Ordinal)), pooledLog);
            for (int i = 0; i < 50; i++)
            {
                vanilla[1000 + i] = i;
                pooled[1000 + i] = i;
            }
            Assert.Equal(vanilla.ToList(), pooled.ToList()); // enumeration order, free-list reuse included

            ReversibleMultiDictionary<int, int> multiVanilla = BuildMulti(seed + round), multiPooled = BuildMulti(seed + round);
            Func<KeyValuePair<int, int>, bool> keepPair = kvp => (kvp.Key + kvp.Value) % 4 != 0;
            List<string> multiVanillaLog = Run(multiVanilla, keepPair, pooled: false);
            Assert.Equal(multiVanillaLog.Where(l => !l.StartsWith("result", StringComparison.Ordinal)), Run(multiPooled, keepPair, pooled: true));
            Assert.Equal(multiVanilla.ToList(), multiPooled.ToList());
        }
    }

    [Fact]
    public void ListArrayAndBadArgumentsTakeVanilla()
    {
        var vanilla = Enumerable.Range(0, 20).ToList();
        var pooled = Enumerable.Range(0, 20).ToList();
        _ = Algorithms.RemoveAll(vanilla, i => i % 3 == 0);
        SplitAlloc.RemoveAll(pooled, i => i % 3 == 0);
        Assert.Equal(vanilla, pooled);
        Assert.Throws<ArgumentNullException>(() => SplitAlloc.RemoveAll<int>(null!, _ => true));
        Assert.Throws<ArgumentNullException>(() => SplitAlloc.RemoveAll(new HashSet<int>(), null!));
        Assert.Throws<ArgumentException>(() => SplitAlloc.RemoveAll(new System.Collections.ObjectModel.ReadOnlyCollection<int>([1, 2]), _ => true));
    }

    /// <summary>A predicate that throws leaves the collection as vanilla does; a nested call on the same T gets its own list.</summary>
    [Fact]
    public void ThrowingAndNestedCalls()
    {
        var set = new HashSet<int>(Enumerable.Range(0, 10));
        Assert.Throws<InvalidOperationException>(() => SplitAlloc.RemoveAll(set, i => i == 5 ? throw new InvalidOperationException() : i < 3));
        Assert.Equal(10, set.Count);
        var inner = new HashSet<int>(Enumerable.Range(0, 10));
        SplitAlloc.RemoveAll(set, i =>
        {
            if (i == 0) SplitAlloc.RemoveAll(inner, j => j % 2 == 0);
            return i >= 5;
        });
        Assert.Equal(Enumerable.Range(0, 5), set.Order());
        Assert.Equal([1, 3, 5, 7, 9], inner.Order());
    }
}
