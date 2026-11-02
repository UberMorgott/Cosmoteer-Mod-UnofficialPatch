using System;
using System.Collections.Generic;
using System.Linq;
using Halfling.Collections;
using Halfling.Scene2D;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

public sealed class SceneBucketRemoveTests
{
    private sealed class Item : IUpdateableSceneObject
    {
        public void Update(SceneRoot root) { }
    }

    private sealed class EqualsItem : IUpdateableSceneObject
    {
        public void Update(SceneRoot root) { }
        public override bool Equals(object? obj) => obj is EqualsItem;
        public override int GetHashCode() => 0;
    }

    [Fact]
    public void KillSwitchIsOnlyZero()
    {
        Assert.False(SceneBucketRemove.IsRequested("0"));
        Assert.True(SceneBucketRemove.IsRequested(null));
        Assert.True(SceneBucketRemove.IsRequested("1"));
    }

    [Fact]
    public void TranspilersMatchRealGameIl()
    {
        foreach (var target in SceneBucketRemove.Targets)
        {
            List<CodeInstruction> code = SceneBucketRemove.Transpiler(PatchProcessor.GetOriginalInstructions(target), target).ToList();
            Assert.Contains(code, ci => ci.operand is System.Reflection.MethodInfo { Name: nameof(SceneBucketRemove.Remove) });
        }
    }

    [Fact]
    public void DetectsEqualsOverrides()
    {
        Assert.False(SceneBucketRemove.OverridesEquals(typeof(Item)));
        Assert.True(SceneBucketRemove.OverridesEquals(typeof(EqualsItem)));
        Assert.False(SceneBucketRemove.OverridesEquals(typeof(string))); // not listable
        Assert.False(SceneBucketRemove.OverridesEquals(typeof(SceneComponent)));
    }

    /// <summary>An Equals-overriding object added through the game's add path switches the fast path off (vanilla removes a for b).</summary>
    [Fact]
    public void AddOfEqualsOverrideKeepsVanilla()
    {
        AccessTools.Field(typeof(SceneBucketRemove), "_referenceEquality").SetValue(null, true);
        try
        {
            var buckets = new BucketList<IUpdateableSceneObject>();
            IUpdateableSceneObject a = new EqualsItem(), b = new EqualsItem();
            SceneBucketRemove.AddChecked(buckets, 0, a);
            SceneBucketRemove.AddChecked(buckets, 0, b);
            Assert.True(SceneBucketRemove.Remove(buckets, 0, b));
            Assert.Same(b, Assert.Single(buckets.GetBucket(0)));
        }
        finally
        {
            AccessTools.Field(typeof(SceneBucketRemove), "_referenceEquality").SetValue(null, true);
        }
    }

    [Fact]
    public void RemoveMatchesVanilla()
    {
        // This test assembly holds EqualsItem, so a real Initialize() would keep vanilla; force the fast path.
        AccessTools.Field(typeof(SceneBucketRemove), "_referenceEquality").SetValue(null, true);
        var rng = new Random(7);
        var pool = Enumerable.Range(0, 40).Select(_ => (IUpdateableSceneObject)new Item()).ToArray();
        var fast = new BucketList<IUpdateableSceneObject>();
        var vanilla = new BucketList<IUpdateableSceneObject>();
        for (int step = 0; step < 5000; step++)
        {
            int bucket = rng.Next(3);
            var item = pool[rng.Next(pool.Length)];
            if (rng.Next(3) == 0)
            {
                fast.Add(bucket, item); // duplicates allowed: both remove the first occurrence
                vanilla.Add(bucket, item);
            }
            else
            {
                Assert.Equal(vanilla.Remove(bucket, item), SceneBucketRemove.Remove(fast, bucket, item));
            }
            Assert.Equal(vanilla.Count, fast.Count);
            for (int b = 0; b < 3; b++)
            {
                Assert.Equal(vanilla.TryGetBucket(b, out var v), fast.TryGetBucket(b, out var f));
                if (v != null) Assert.Equal(v, f!);
            }
        }
    }
}
