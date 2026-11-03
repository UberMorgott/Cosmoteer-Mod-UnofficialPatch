using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Cosmoteer.Simulation.Cameras;
using HarmonyLib;
using Xunit;

namespace UnofficialPatch.Tests;

public sealed class ZoomSubscribersTests
{
    [Fact]
    public void KillSwitchIsOnlyZero()
    {
        Assert.False(ZoomSubscribers.IsRequested("0"));
        Assert.True(ZoomSubscribers.IsRequested(null));
    }

    [Fact]
    public void TranspilersMatchRealGameIl()
    {
        foreach (var target in ZoomSubscribers.Targets)
            Assert.NotEmpty(ZoomSubscribers.Transpiler(PatchProcessor.GetOriginalInstructions(target), target).ToList());
    }

    private sealed class Target(int id, List<int> log)
    {
        public void OnZoom(object? sender, EventArgs e) => log.Add(id);
    }

    /// <summary>Random subscribe/unsubscribe (with duplicates and unknown removes) raises in the same order as a multicast delegate.</summary>
    [Fact]
    public void MatchesMulticastSemantics()
    {
        var camera = (CameraNode)RuntimeHelpers.GetUninitializedObject(typeof(CameraNode));
        var vanillaLog = new List<int>();
        var fastLog = new List<int>();
        var vanillaTargets = Enumerable.Range(0, 30).Select(i => new Target(i, vanillaLog)).ToArray();
        var fastTargets = Enumerable.Range(0, 30).Select(i => new Target(i, fastLog)).ToArray();
        EventHandler<EventArgs>? vanilla = null;
        var rng = new Random(3);
        for (int step = 0; step < 4000; step++)
        {
            int i = rng.Next(30);
            switch (rng.Next(5))
            {
                case 0 or 1:
                    vanilla += vanillaTargets[i].OnZoom;
                    ZoomSubscribers.Add(camera, fastTargets[i].OnZoom);
                    break;
                case 2 or 3:
                    vanilla -= vanillaTargets[i].OnZoom;
                    ZoomSubscribers.Remove(camera, fastTargets[i].OnZoom);
                    break;
                default:
                    vanilla?.Invoke(null, EventArgs.Empty);
                    AccessTools.Field(typeof(CameraNode), "ZoomChanged").GetValue(camera).As<EventHandler<EventArgs>?>()?.Invoke(null, EventArgs.Empty);
                    Assert.Equal(vanillaLog, fastLog);
                    break;
            }
        }
        Assert.NotEmpty(vanillaLog);
    }
}

internal static class AsExtension
{
    internal static T As<T>(this object? value) => (T)value!;
}
