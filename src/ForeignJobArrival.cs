using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Ships.Crew;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>
/// Vanilla race: ShipCrewManager (bucket -13, ships in parallel) runs a crew's job work inline only when the crew is on
/// its home ship AND the job belongs to that ship (UpdateCrewJobs), else defers it with EnqueueDeterministic(Ship.UniqueID).
/// ShipCrew.UpdatePostMovement's two gates (path-failure UnassignCrew, arrival OnCrewArrived) check only the home ship, so a
/// crew working another ship's job (same-player transfers, construction helper ships) mutates that job (crew lists,
/// requested/in-hand counts) on its own ship's thread while the job's ship touches it on another. Both gates now use
/// UpdateCrewJobs' condition; foreign-job cases fall into vanilla's own deferred branch. Every MP player must run this mod.
/// </summary>
internal static class ForeignJobArrival
{
    private const string IlSha256 = "AE0C5DC868D40BD7B4B0D19988D0FD9584C234E3C1D8BB429A575BF71ABE797F";

    private static readonly MethodInfo IsOnHomeShip =
        AccessTools.PropertyGetter(typeof(ShipCrew), nameof(ShipCrew.IsOnHomeShip))
        ?? throw new MissingMethodException(nameof(ShipCrew), nameof(ShipCrew.IsOnHomeShip));

    internal static MethodInfo Target =>
        AccessTools.Method(typeof(ShipCrew), "Cosmoteer.Ships.Crew.ShipCrewManager.IShipCrewMembers.UpdatePostMovement")
        ?? throw new MissingMethodException(nameof(ShipCrew), "IShipCrewMembers.UpdatePostMovement");

    /// <summary>Exactly vanilla's body with both IsOnHomeShip gates swapped; any IL change rolls the mod back to vanilla.</summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = Patcher.RequireIl(instructions, IlSha256, "ShipCrew.UpdatePostMovement");
        MethodInfo gate = AccessTools.Method(typeof(ForeignJobArrival), nameof(RunsInline));
        List<CodeInstruction> calls = code.Where(c => c.Calls(IsOnHomeShip)).ToList();
        if (calls.Count != 2)
        {
            throw new InvalidOperationException($"ShipCrew.UpdatePostMovement: expected 2 IsOnHomeShip gates, found {calls.Count}");
        }
        calls.ForEach(c => (c.opcode, c.operand) = (OpCodes.Call, gate));
        return code;
    }

    /// <summary>ShipCrewManager.UpdateCrewJobs' inline condition: on the home ship and the job (if any) is that ship's.</summary>
    public static bool RunsInline(ShipCrew crew) =>
        crew.IsOnHomeShip && (crew.Soul.Job is not { } job || job.Ship == crew.Ship);
}
