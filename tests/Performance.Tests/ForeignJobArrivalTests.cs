using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Crew;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Crew;
using Cosmoteer.Ships.Crew.Jobs;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

public class ForeignJobArrivalTests
{
    [Fact]
    public void TranspilerSwapsExactlyBothHomeShipGates()
    {
        var original = PatchProcessor.GetOriginalInstructions(ForeignJobArrival.Target);
        var vanilla = original.Select(c => c.ToString()).ToList();

        var patched = ForeignJobArrival.Transpiler(original).ToList();

        var gate = AccessTools.Method(typeof(ForeignJobArrival), nameof(ForeignJobArrival.RunsInline));
        Assert.Equal(2, patched.Count(c => c.Calls(gate)));
        Assert.DoesNotContain(patched, c => c.operand is System.Reflection.MethodInfo { Name: "get_IsOnHomeShip" });
        Assert.Equal(vanilla.Count, patched.Count);
        Assert.Equal(vanilla.Count - 2, vanilla.Zip(patched, (v, p) => v == p.ToString()).Count(same => same));
    }

    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static void Set(object target, string field, object? value) =>
        AccessTools.Field(target.GetType(), field).SetValue(target, value);

    // Crew standing on `current`, home `home`, holding a job of `jobShip` (no job when jobShip is "none").
    [Theory]
    [InlineData("A", "A", "none", true)]
    [InlineData("A", "A", "A", true)]
    [InlineData("A", "A", "B", false)] // the fixed race: foreign job on the home ship is deferred, like UpdateCrewJobs
    [InlineData("B", "A", "B", false)] // off the home ship: vanilla already deferred
    [InlineData("B", "A", "none", false)]
    public void RunsInlineMatchesUpdateCrewJobsCondition(string current, string home, string jobShip, bool expected)
    {
        var ships = new Dictionary<string, Ship> { ["A"] = New<Ship>(), ["B"] = New<Ship>() };
        var soul = New<CrewSoul>();
        Set(soul, "<HomeShip>k__BackingField", ships[home]);
        if (jobShip != "none")
        {
            var job = New<ResourceTransferJob>();
            AccessTools.Field(typeof(Job), "<Ship>k__BackingField").SetValue(job, ships[jobShip]);
            Set(soul, "<Job>k__BackingField", job);
        }
        var crew = New<ShipCrew>();
        Set(crew, "<Soul>k__BackingField", soul);
        Set(crew, "p_ship", ships[current]);

        Assert.Equal(expected, ForeignJobArrival.RunsInline(crew));
    }

    [Fact]
    public void TranspilerRejectsChangedIl()
    {
        var original = PatchProcessor.GetOriginalInstructions(ForeignJobArrival.Target);
        original.Insert(0, new CodeInstruction(OpCodes.Nop));

        Assert.Throws<InvalidOperationException>(() => ForeignJobArrival.Transpiler(original).ToList());
    }
}
