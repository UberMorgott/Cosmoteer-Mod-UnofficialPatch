// Port of vanilla World.SolveTOI: keeps the game's (unannotated) null assumptions.
#nullable disable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Halfling;
using Halfling.Performance;
using Halfling.Physics2D;
using Halfling.Physics2D.Collision;
using Halfling.Physics2D.Dynamics;
using Halfling.Physics2D.Dynamics.Contacts;
using System.Numerics;
using Halfling.Physics2D.Common;
using HarmonyLib;

namespace Performance;

/// <summary>
/// After every physics step, <c>World.SolveTOI</c> (Box2D continuous collision) looks for the earliest time of impact:
/// each pass walks the whole contact list and, for every contact without a cached time, re-checks whether it needs CCD
/// at all (sensors, sleeping/static bodies, bullet flags and relative velocity of both bodies). Every ship is a CCD
/// "bullet", so in a battle nearly every contact is checked and rejected again on every pass, and there is one pass per
/// bullet impact plus one. This was the largest single cost of a combat tick (about 18% of the main thread).
/// <para/>
/// <see cref="Solve"/> is vanilla SolveTOI with one change per pass: the rejection checks, which only read state, run
/// for all contacts in parallel first and record per contact "skip", "cached" or "evaluate"; then the vanilla pass runs
/// serially in list order over that record, doing the sweep advance and time-of-impact work for "evaluate" contacts
/// exactly as before. Nothing these checks read changes inside a pass (a sweep advance moves only C0/A0/Alpha0), so the
/// chosen contact, every side effect and their order are identical. The contact list is mirrored in an array that is
/// extended with the contacts FindNewContacts inserts at the list head (the world is locked: nothing is destroyed).
/// Off: <c>PERFORMANCE_TOI_SCAN=0</c>.
/// </summary>
internal static class PhysicsToiScan
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_TOI_SCAN"));
    internal static bool IsRequested(string value) => value != "0";

    private const string IlSha256 = "7AD052D6628700C617206C5DF5810C4D69DF6FF0EDC8CD09E3E0FBEE674F18DB";

    internal static readonly MethodInfo Target = AccessTools.Method(typeof(World), "SolveTOI")
        ?? throw new MissingMethodException(nameof(World), "SolveTOI");

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? [(Target, typeof(PhysicsToiScan))] : [];

    /// <summary>Replaces the whole body with <c>Solve(this, ref step, ref iterations)</c>.</summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        _ = Patcher.RequireIl(instructions, IlSha256, "World.SolveTOI");
        return
        [
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Ldarg_2),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PhysicsToiScan), nameof(Solve))),
            new CodeInstruction(OpCodes.Ret),
        ];
    }

    private const byte Skip = 0, Cached = 1, Evaluate = 2;

    /// <summary>Contacts below this count are classified on the calling thread (same result, no dispatch cost).</summary>
    internal static int ParallelThreshold = 512; // tests lower it

    /// <summary>
    /// Per-call contact mirror. Pooled per nesting level: a callback inside the TOI loop may step another world, which
    /// takes its own scratch (main thread only, like World.Step).
    /// </summary>
    private sealed class Scratch
    {
        internal Contact[] Reversed = new Contact[256]; // list order = Reversed[Count-1] .. Reversed[0]
        internal byte[] Kind = new byte[256];
        internal float[] Toi = new float[256]; // _toi of Cached contacts, read during classification
        internal int Count;
    }

    private static readonly Stack<Scratch> Pool = new();

    private static readonly FastParallelAction ClassifyBatch = (from, to, data) =>
    {
        if (data is not Scratch scratch) return; // always a Scratch (ClassifyAll)
        Contact[] contacts = scratch.Reversed;
        byte[] kind = scratch.Kind;
        float[] toi = scratch.Toi;
        for (int i = from; i < to; i++)
        {
            Contact c = contacts[i];
            byte k = Classify(c);
            kind[i] = k;
            if (k == Cached) toi[i] = c._toi; // a pass writes _toi only of Evaluate contacts
        }
    };
    /// <summary>Vanilla's per-contact checks before the sweep advance, as a pure read.</summary>
    internal static byte Classify(Contact c)
    {
        if (!c.Enabled || c._toiCount > Settings.MaxSubSteps) return Skip;
        if (c.TOIFlag) return Cached;
        Fixture fixtureA = c.FixtureA;
        Fixture fixtureB = c.FixtureB;
        if (fixtureA.IsSensor || fixtureB.IsSensor) return Skip;
        Body body = fixtureA.Body ?? throw new InvalidOperationException("contact fixture without body"); // vanilla would throw too
        Body body2 = fixtureB.Body ?? throw new InvalidOperationException("contact fixture without body");
        BodyType bodyType = body.BodyType;
        BodyType bodyType2 = body2.BodyType;
        bool flag = body.Awake && bodyType != BodyType.Static;
        bool flag2 = body2.Awake && bodyType2 != BodyType.Static;
        if (!flag && !flag2) return Skip;
        bool flag3 = !body.IsBullet || body.CCDThresholdVelocity > 0f;
        bool flag4 = !body2.IsBullet || body2.CCDThresholdVelocity > 0f;
        float num3;
        if ((body.IsBullet || body2.IsBullet) & flag3 & flag4)
        {
            Vector2 v1 = body.GetLinearVelocityFromLocalPoint(fixtureA.Shape.MassData.Centroid);
            Vector2 v2 = body2.GetLinearVelocityFromLocalPoint(fixtureB.Shape.MassData.Centroid);
            num3 = (v1 - v2).LengthSquared();
        }
        else
        {
            num3 = 0f;
        }
        bool flag5 = ((body.IsBullet && num3 >= body.CCDThresholdVelocity * body.CCDThresholdVelocity) || bodyType != BodyType.Dynamic) && !body.IgnoreCCD;
        bool flag6 = ((body2.IsBullet && num3 >= body2.CCDThresholdVelocity * body2.CCDThresholdVelocity) || bodyType2 != BodyType.Dynamic) && !body2.IgnoreCCD;
        return flag5 || flag6 ? Evaluate : Skip;
    }

    /// <summary>Mirrors the contact list; only head insertions since the last sync are appended, anything else rebuilds.</summary>
    private static void Sync(Scratch s, ContactManager manager, bool rebuild)
    {
        int total = manager.ContactCount;
        int added = total - s.Count;
        if (rebuild || added < 0 || (s.Count > 0 && !ReferenceEquals(Nth(manager, added), s.Reversed[s.Count - 1])))
        {
            s.Count = 0;
            added = total;
        }
        if (added == 0) return;
        if (s.Reversed.Length < total)
        {
            Array.Resize(ref s.Reversed, Math.Max(total, s.Reversed.Length * 2));
            s.Kind = new byte[s.Reversed.Length];
            s.Toi = new float[s.Reversed.Length];
        }
        // The list starts with the newest contacts: fill the new tail so that list order is preserved when read backwards.
        Contact c = manager.ContactList.Next;
        for (int i = s.Count + added - 1; i >= s.Count; i--, c = c.Next) s.Reversed[i] = c;
        s.Count += added;
    }

    /// <summary>The n-th contact of the list (0 = head), or null when the list is shorter.</summary>
    private static Contact Nth(ContactManager manager, int n)
    {
        Contact c = manager.ContactList.Next;
        for (; n > 0 && c != manager.ContactList; n--) c = c.Next;
        return c == manager.ContactList ? null : c;
    }

    // Main thread only; logged every 60 s so the threshold and the pass count can be checked in game logs.
    private static long _calls, _passes, _classified, _parallelPasses, _lastLog = System.Diagnostics.Stopwatch.GetTimestamp();

    private static void ClassifyAll(Scratch s)
    {
        _passes++;
        _classified += s.Count;
        if (s.Count >= ParallelThreshold) _parallelPasses++;
        if (s.Count >= ParallelThreshold && FastParallel.IsRunning && FastParallel.ThreadCount > 0)
            FastParallel.For(0, s.Count, ClassifyBatch, s);
        else
            ClassifyBatch(0, s.Count, s);
    }
    private static void LogStats()
    {
        _lastLog = System.Diagnostics.Stopwatch.GetTimestamp();
        Log.Info(FormattableString.Invariant($"PhysicsToiScan: {_calls} steps, {_passes / (double)_calls:F2} passes/step, {_classified / (double)Math.Max(1, _passes):F0} contacts/pass, {_parallelPasses} parallel passes"));
    }

    /// <summary>Vanilla World.SolveTOI; the contact scan reads the parallel classification.</summary>
    internal static void Solve(World world, ref TimeStep step, ref SolverIterations iterations)
    {
        ContactManager contactManager = world.ContactManager;
        world.Island.Reset(2 * Settings.MaxTOIContacts, Settings.MaxTOIContacts, 0, contactManager);
        if (world._stepComplete)
        {
            List<Body> bodies = world.BodyList;
            for (int i = 0; i < bodies.Count; i++)
            {
                bodies[i]._island = false;
                bodies[i]._sweep.Alpha0 = 0f;
            }
            for (Contact next = contactManager.ContactList.Next; next != contactManager.ContactList; next = next.Next)
            {
                next.IslandFlag = false;
                next.TOIFlag = false;
                next._toiCount = 0;
                next._toi = 1f;
            }
        }
        Scratch scratch = Pool.Count > 0 ? Pool.Pop() : new Scratch();
        if (++_calls % 64 == 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastLog).TotalSeconds >= 60) LogStats();
        Sync(scratch, contactManager, rebuild: true);
        try
        {
            TimeStep subStep = default;
            while (true)
            {
                ClassifyAll(scratch);
                Contact contact = null;
                float num = 1f;
                Contact[] contacts = scratch.Reversed;
                byte[] kind = scratch.Kind;
                float[] toiCache = scratch.Toi;
                for (int i = scratch.Count - 1; i >= 0; i--)
                {
                    byte k = kind[i];
                    if (k == Skip) continue;
                    Contact next2 = contacts[i];
                    float num2;
                    if (k == Cached)
                    {
                        num2 = toiCache[i];
                    }
                    else
                    {
                        Fixture fixtureA = next2.FixtureA;
                        Fixture fixtureB = next2.FixtureB;
                        Body body = fixtureA.Body ?? throw new InvalidOperationException("contact fixture without body");
                        Body body2 = fixtureB.Body ?? throw new InvalidOperationException("contact fixture without body");
                        float alpha = body._sweep.Alpha0;
                        if (body._sweep.Alpha0 < body2._sweep.Alpha0)
                        {
                            alpha = body2._sweep.Alpha0;
                            body._sweep.Advance(alpha);
                        }
                        else if (body2._sweep.Alpha0 < body._sweep.Alpha0)
                        {
                            alpha = body._sweep.Alpha0;
                            body2._sweep.Advance(alpha);
                        }
                        world._input.ProxyA.Reset(fixtureA.Shape, next2.ChildIndexA);
                        world._input.ProxyB.Reset(fixtureB.Shape, next2.ChildIndexB);
                        world._input.SweepA = body._sweep;
                        world._input.SweepB = body2._sweep;
                        world._input.TMax = 1f;
                        TimeOfImpact.CalculateTimeOfImpact(out TOIOutput output, ref world._input);
                        float t = output.T;
                        num2 = next2._toi = output.State != TOIOutputState.Touching ? 1f : Mathx.Min(alpha + (1f - alpha) * t, 1f);
                        next2.TOIFlag = true;
                    }
                    if (num2 < num)
                    {
                        contact = next2;
                        num = num2;
                    }
                }
                if (contact == null || 0.9999988f < num) break;
                Fixture fixtureA2 = contact.FixtureA;
                Fixture fixtureB2 = contact.FixtureB;
                Body body3 = fixtureA2.Body ?? throw new InvalidOperationException("contact fixture without body");
                Body body4 = fixtureB2.Body ?? throw new InvalidOperationException("contact fixture without body");
                Sweep sweep = body3._sweep;
                Sweep sweep2 = body4._sweep;
                body3.Advance(num);
                body4.Advance(num);
                contact.Update(contactManager);
                contact.TOIFlag = false;
                contact._toiCount++;
                if (!contact.Enabled || !contact.IsTouching)
                {
                    contact.Enabled = false;
                    body3._sweep = sweep;
                    body4._sweep = sweep2;
                    body3.SynchronizeTransform();
                    body4.SynchronizeTransform();
                    continue;
                }
                body3.Awake = true;
                body4.Awake = true;
                Island island = world.Island;
                island.Clear();
                island.Add(body3);
                island.Add(body4);
                island.Add(contact);
                body3._island = true;
                body4._island = true;
                contact.IslandFlag = true;
                for (int j = 0; j < 2; j++)
                {
                    Body body5 = j == 0 ? body3 : body4;
                    if (body5.BodyType != BodyType.Dynamic) continue;
                    for (ContactEdge contactEdge = body5.ContactList; contactEdge != null; contactEdge = contactEdge.Next)
                    {
                        Contact contact2 = contactEdge.Contact;
                        if (island.BodyCount == island.BodyCapacity || island.ContactCount == island.ContactCapacity) break;
                        if (contact2.IslandFlag) continue;
                        Body other = contactEdge.Other;
                        if ((other.BodyType != BodyType.Dynamic || body5.IsBullet || other.IsBullet) && !contact2.FixtureA.IsSensor && !contact2.FixtureB.IsSensor)
                        {
                            Sweep sweep3 = other._sweep;
                            if (!other._island) other.Advance(num);
                            contact2.Update(contactManager);
                            if (!contact2.Enabled)
                            {
                                other._sweep = sweep3;
                                other.SynchronizeTransform();
                            }
                            else if (!contact2.IsTouching)
                            {
                                other._sweep = sweep3;
                                other.SynchronizeTransform();
                            }
                            else
                            {
                                contact2.IslandFlag = true;
                                island.Add(contact2);
                                if (!other._island)
                                {
                                    other._island = true;
                                    if (other.BodyType != BodyType.Static) other.Awake = true;
                                    island.Add(other);
                                }
                            }
                        }
                    }
                }
                subStep.positionIterations = iterations.TOIPositionIterations;
                subStep.velocityIterations = iterations.TOIVelocityIterations;
                subStep.dt = (1f - num) * step.dt;
                subStep.inv_dt = 1f / subStep.dt;
                subStep.dtRatio = 1f;
                subStep.warmStarting = false;
                island.SolveTOI(ref subStep, body3.IslandIndex, body4.IslandIndex);
                for (int k = 0; k < island.BodyCount; k++)
                {
                    Body body6 = island.Bodies[k];
                    body6._island = false;
                    if (body6.BodyType == BodyType.Dynamic)
                    {
                        body6.Synchronize();
                        for (ContactEdge contactEdge2 = body6.ContactList; contactEdge2 != null; contactEdge2 = contactEdge2.Next)
                        {
                            contactEdge2.Contact.TOIFlag = false;
                            contactEdge2.Contact.IslandFlag = false;
                        }
                    }
                }
                contactManager.FindNewContacts();
                Sync(scratch, contactManager, rebuild: false);
            }
            world._stepComplete = true;
        }
        finally
        {
            Array.Clear(scratch.Reversed, 0, scratch.Count); // keep no contact alive between steps
            scratch.Count = 0;
            Pool.Push(scratch);
        }
    }
}
