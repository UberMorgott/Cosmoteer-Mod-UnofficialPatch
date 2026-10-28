using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Crew;
using Cosmoteer.Game;
using Cosmoteer.Modes.Career;
using Cosmoteer.Ships.Crew.Jobs;
using Cosmoteer.Simulation;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

public class MissionTradeTests
{
    public static TheoryData<int> Targets() => new(Enumerable.Range(0, MissionTrade.Targets.Length));

    [Theory]
    [MemberData(nameof(Targets))]
    public void TranspilerMatchesRealGameIlUnchanged(int i)
    {
        var target = MissionTrade.Targets[i].Target;
        var original = PatchProcessor.GetOriginalInstructions(target);

        Assert.Equal(original.Select(c => c.ToString()), MissionTrade.Transpiler(original, target).Select(c => c.ToString()));
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void TranspilerRejectsChangedIl(int i)
    {
        var target = MissionTrade.Targets[i].Target;
        var original = PatchProcessor.GetOriginalInstructions(target);
        original.Insert(0, new CodeInstruction(OpCodes.Nop));

        Assert.Throws<InvalidOperationException>(() => MissionTrade.Transpiler(original, target).ToList());
    }

    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static (CareerSimModeManager Mode, SimRoot Sim) NewMode()
    {
        var sim = New<SimRoot>();
        Set(typeof(SimRoot), sim, nameof(SimRoot._queuedDeterministic), new List<SimRoot.QueuedAction>());
        Set(typeof(SimRoot), sim, nameof(SimRoot._queuedHitEffects), new List<SimRoot.QueuedHitEffects>());
        Set(typeof(SimRoot), sim, nameof(SimRoot._queuedNonDeterministic), new ConcurrentQueue<Action>());
        var mode = New<CareerSimModeManager>();
        Set(typeof(Cosmoteer.Modes.SimModeManager), mode, "<Sim>k__BackingField", sim);
        return (mode, sim);
    }

    // Readonly fields of uninitialized objects: only reflection can set them.
    private static void Set(Type type, object target, string field, object value) =>
        AccessTools.Field(type, field).SetValue(target, value);

    private static CrewSoul NewCrew(uint id)
    {
        var form = New<SpaceCrew>();
        form.UniqueID = default(ObjectID) + id;
        var crew = New<CrewSoul>();
        crew.Form = form;
        return crew;
    }

    [Fact]
    public void SerialCallRunsVanilla()
    {
        var (mode, sim) = NewMode();

        Assert.True(MissionTrade.Prefix(mode, MissionTrade.Targets[1].Target, TradeType.Sell, default, 1, NewCrew(1), null, null));
        Assert.Empty(sim._queuedDeterministic);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void ParallelCallsAreQueuedInCrewFormIdOrderAndReplaySerially(int i)
    {
        var (mode, sim) = NewMode();
        sim.IsDoingParallelUpdate = true;

        foreach (uint id in new uint[] { 7, 3, 7, 5 })
        {
            Assert.False(MissionTrade.Prefix(mode, MissionTrade.Targets[i].Target, TradeType.Sell, default, 1, NewCrew(id), null, null));
        }
        sim._queuedDeterministic.Sort();
        Assert.Equal([3u, 5u, 7u, 7u], sim._queuedDeterministic.Select(q => (uint)q._shipID));
        Assert.Equal([0, 2], sim._queuedDeterministic.Where(q => (uint)q._shipID == 7).Select(q => q._actionID));

        // Replay re-enters the patched method outside the parallel update: vanilla runs (null sink/source ship: no-op), nothing requeued.
        sim.IsDoingParallelUpdate = false;
        sim.ExecuteQueued(deterministic: true, nonDeterministic: false);
        Assert.Empty(sim._queuedDeterministic);
    }
}
