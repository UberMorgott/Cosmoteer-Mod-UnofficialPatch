using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Cosmoteer.Data;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Paint;
using Halfling.Geometry;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

public sealed class DecalLookupTests
{
    [Fact]
    public void KillSwitchIsOnlyZero()
    {
        Assert.False(DecalLookup.IsRequested("0"));
        Assert.True(DecalLookup.IsRequested(null));
        Assert.True(DecalLookup.IsRequested("1"));
    }

    [Fact]
    public void RuntimeInternalsFoundAndTargetNotShared()
    {
        Assert.True(DecalLookup.Available);
        Assert.False(SharedGenericRetarget.IsShared(DecalLookup.Target));
        Assert.Single(DecalLookup.Patches);
    }

    /// <summary>Against the real game IL: exactly the one RoofDecals indexer call is redirected, everything else unchanged.</summary>
    [Fact]
    public void TranspilerRedirectsOnlyTheLookup()
    {
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(DecalLookup.Target);
        List<CodeInstruction> code = DecalLookup.Transpiler(PatchProcessor.GetOriginalInstructions(DecalLookup.Target)).ToList();
        Assert.Equal(original.Count, code.Count);
        int changed = 0;
        for (int i = 0; i < code.Count; i++)
        {
            if (code[i].opcode == original[i].opcode && code[i].operand?.ToString() == original[i].operand?.ToString()) continue;
            changed++;
            Assert.Equal(OpCodes.Callvirt, original[i].opcode);
            Assert.Equal(DecalLookup.NativeGetItem, original[i].operand);
            Assert.Equal(OpCodes.Call, code[i].opcode);
            Assert.Equal(DecalLookup.Replacement, code[i].operand);
            Assert.Equal(original[i].labels.Count, code[i].labels.Count);
        }
        Assert.Equal(1, changed);
    }

    [Fact]
    public void TranspilerRejectsChangedIl()
    {
        List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(DecalLookup.Target);
        code.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => DecalLookup.Transpiler(code).ToList());
    }

    private static RoofDecalInfo Info(int i) => new(new Rect(i, i + 1, i + 2, i + 3), new IntVector2(i, i + 1), new IntVector2(i + 2, i + 3), i);

    private static Exception? Catch(Func<RoofDecalInfo> f, out RoofDecalInfo value)
    {
        value = default;
        try
        {
            value = f();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static void AssertSame(SortedDictionary<ID<Decal>, RoofDecalInfo>? dict, ID<Decal> key)
    {
        Exception? nativeEx = Catch(() => dict![key], out RoofDecalInfo native);
        Exception? oursEx = Catch(() => DecalLookup.Get(dict!, key), out RoofDecalInfo ours);
        Assert.Equal(nativeEx?.GetType(), oursEx?.GetType());
        Assert.Equal(nativeEx?.Message, oursEx?.Message);
        Assert.Equal(native.UVRect, ours.UVRect);
        Assert.Equal(native.DefaultSize, ours.DefaultSize);
        Assert.Equal(native.Rot0MaxSize, ours.Rot0MaxSize);
        Assert.Equal(native.SubLayerIndex, ours.SubLayerIndex);
    }

    /// <summary>
    /// Differential: random rules dictionaries (present, missing, empty-name and unregistered-index keys) looked up natively
    /// and through the cache, with mutations (add, remove, overwrite, clear) and rules swaps in between.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SameResultAsNative(int seed)
    {
        var rng = new Random(seed);
        ID<Decal>[] pool = Enumerable.Range(0, 40).Select(i => new ID<Decal>($"perf_decal_lookup_{seed}_{i}_{(char)('a' + rng.Next(26))}")).ToArray();
        ID<Decal>[] extras = [new ID<Decal>(""), new ID<Decal>("PERF_DECAL_LOOKUP_" + seed + "_0_A"), (ID<Decal>)int.MaxValue];
        SortedDictionary<ID<Decal>, RoofDecalInfo> dict = Fill(rng, pool);
        int checks = 0;
        for (int step = 0; step < 2000; step++)
        {
            switch (rng.Next(10))
            {
                case 0:
                    dict = Fill(rng, pool); // rules swap: new rules object, new dictionary
                    break;
                case 1:
                    dict[pool[rng.Next(pool.Length)]] = Info(rng.Next(1000));
                    break;
                case 2:
                    dict.Remove(pool[rng.Next(pool.Length)]);
                    break;
                case 3 when rng.Next(20) == 0:
                    dict.Clear();
                    break;
                default:
                    ID<Decal> key = rng.Next(12) == 0 ? extras[rng.Next(extras.Length)] : pool[rng.Next(pool.Length)];
                    AssertSame(dict, key);
                    checks++;
                    break;
            }
        }
        Assert.True(checks > 1000);
        AssertSame(null, pool[0]);
    }

    [Fact]
    public void CacheIsPerDictionaryAndFollowsMutation()
    {
        var a = new ID<Decal>("perf_decal_lookup_cache_a");
        var b = new ID<Decal>("perf_decal_lookup_cache_b");
        var first = new SortedDictionary<ID<Decal>, RoofDecalInfo> { [a] = Info(1) };
        var second = new SortedDictionary<ID<Decal>, RoofDecalInfo> { [a] = Info(2), [b] = Info(3) };
        Assert.Equal(1, DecalLookup.Get(first, a).SubLayerIndex);
        Assert.Equal(2, DecalLookup.Get(second, a).SubLayerIndex);
        Assert.Equal(1, DecalLookup.SnapshotCount(first));
        Assert.Equal(2, DecalLookup.SnapshotCount(second));
        Assert.Throws<KeyNotFoundException>(() => DecalLookup.Get(first, b));
        first[b] = Info(4);
        Assert.Equal(4, DecalLookup.Get(first, b).SubLayerIndex);
        first[a] = Info(5);
        Assert.Equal(5, DecalLookup.Get(first, a).SubLayerIndex);
        first.Remove(a);
        Assert.Throws<KeyNotFoundException>(() => DecalLookup.Get(first, a));
    }

    [Fact]
    public void CustomComparerUsesNative()
    {
        var a = new ID<Decal>("perf_decal_lookup_custom_a");
        var dict = new SortedDictionary<ID<Decal>, RoofDecalInfo>(Comparer<ID<Decal>>.Create((x, y) => x.CompareTo(y))) { [a] = Info(7) };
        Assert.Equal(7, DecalLookup.Get(dict, a).SubLayerIndex);
        Assert.Equal(-1, DecalLookup.SnapshotCount(dict));
    }

    private static SortedDictionary<ID<Decal>, RoofDecalInfo> Fill(Random rng, ID<Decal>[] pool)
    {
        var dict = new SortedDictionary<ID<Decal>, RoofDecalInfo>();
        foreach (ID<Decal> id in pool)
        {
            if (rng.Next(3) != 0) dict[id] = Info(rng.Next(1000));
        }
        return dict;
    }
}
