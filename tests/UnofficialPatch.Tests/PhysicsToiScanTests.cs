using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Halfling.Physics2D.Dynamics;
using Halfling.Physics2D.Dynamics.Contacts;
using HarmonyLib;
using Xunit;

namespace UnofficialPatch.Tests;

public sealed class PhysicsToiScanTests
{
    private const string TestId = "morgott.unofficialpatch.tests.toi";

    [Fact]
    public void KillSwitchIsOnlyZero()
    {
        Assert.False(PhysicsToiScan.IsRequested("0"));
        Assert.True(PhysicsToiScan.IsRequested(null!));
    }

    [Fact]
    public void TranspilerMatchesRealGameIl()
    {
        List<CodeInstruction> code = PhysicsToiScan.Transpiler(PatchProcessor.GetOriginalInstructions(PhysicsToiScan.Target)).ToList();
        Assert.Equal(5, code.Count);
    }

    /// <summary>Fast CCD bullets and slow CCD "ships" (bullet flag + threshold) colliding: bit-identical to vanilla.</summary>
    [Fact]
    public void SolveMatchesVanillaBitForBit()
    {
        string vanilla = Simulate();
        var harmony = new Harmony(TestId);
        try
        {
            harmony.Patch(PhysicsToiScan.Target, transpiler: new HarmonyMethod(typeof(PhysicsToiScan), nameof(PhysicsToiScan.Transpiler)));
            harmony.Patch(AccessTools.Method(typeof(Island), nameof(Island.SolveTOI)), prefix: new HarmonyMethod(typeof(PhysicsToiScanTests), nameof(CountToi)));
            Assert.Equal(vanilla, SimulateVerified());
            Assert.True(_toiEvents > 20, $"only {_toiEvents} TOI events");
            PhysicsToiScan.Incremental = false;
            Assert.Equal(vanilla, Simulate());
        }
        finally
        {
            PhysicsToiScan.Incremental = true;
            harmony.UnpatchAll(TestId);
        }
    }

    /// <summary>Incremental classification with the full-classification check on; any stale contact fails the test.</summary>
    private static string SimulateVerified(bool nested = false, bool remote = false)
    {
        long before = PhysicsToiScan.VerifyMismatches;
        PhysicsToiScan.Verify = true;
        try { return Simulate(nested, remote); }
        finally
        {
            PhysicsToiScan.Verify = false;
            Assert.Equal(before, PhysicsToiScan.VerifyMismatches);
        }
    }

    /// <summary>OnCollision callbacks inside the TOI loop change bodies that are not part of the contact.</summary>
    [Fact]
    public void RemoteBodyChangesFromCallbacksMatchVanilla()
    {
        var harmony = new Harmony(TestId + ".remote");
        try
        {
            harmony.Patch(PhysicsToiScan.Target, prefix: new HarmonyMethod(typeof(PhysicsToiScanTests), nameof(EnterToi)), finalizer: new HarmonyMethod(typeof(PhysicsToiScanTests), nameof(LeaveToi)));
            _remoteChanges = 0;
            string vanilla = Simulate(remote: true);
            Assert.True(_remoteChanges > 10, $"only {_remoteChanges} remote changes from TOI callbacks");
            harmony.Patch(PhysicsToiScan.Target, transpiler: new HarmonyMethod(typeof(PhysicsToiScan), nameof(PhysicsToiScan.Transpiler)));
            Assert.Equal(vanilla, SimulateVerified(remote: true));
        }
        finally
        {
            harmony.UnpatchAll(TestId + ".remote");
        }
    }

    private static int _remoteChanges;

    [Fact]
    public void ParallelClassificationMatchesVanilla()
    {
        string vanilla = Simulate();
        var harmony = new Harmony(TestId + ".parallel");
        int threshold = PhysicsToiScan.ParallelThreshold;
        bool started = !Halfling.Performance.FastParallel.IsRunning;
        try
        {
            if (started) Halfling.Performance.FastParallel.Start();
            Assert.True(Halfling.Performance.FastParallel.ThreadCount > 0);
            PhysicsToiScan.ParallelThreshold = 2;
            harmony.Patch(PhysicsToiScan.Target, transpiler: new HarmonyMethod(typeof(PhysicsToiScan), nameof(PhysicsToiScan.Transpiler)));
            Assert.Equal(vanilla, Simulate());
        }
        finally
        {
            harmony.UnpatchAll(TestId + ".parallel");
            PhysicsToiScan.ParallelThreshold = threshold;
            if (started) Halfling.Performance.FastParallel.Stop();
        }
    }

    private static int _toiEvents;

    private static void CountToi() => _toiEvents++;

    /// <summary>A PreSolve callback inside the TOI loop steps another world (nested SolveTOI), then disables the contact.</summary>
    [Fact]
    public void NestedWorldStepMatchesVanilla()
    {
        var harmony = new Harmony(TestId + ".nested");
        try
        {
            // Marks the outer SolveTOI in both arms, so the callback acts only inside the TOI loop (not in Collide).
            harmony.Patch(PhysicsToiScan.Target, prefix: new HarmonyMethod(typeof(PhysicsToiScanTests), nameof(EnterToi)), finalizer: new HarmonyMethod(typeof(PhysicsToiScanTests), nameof(LeaveToi)));
            _nestedSteps = 0;
            string vanilla = Simulate(nested: true);
            Assert.True(_nestedSteps > 3, $"only {_nestedSteps} nested steps from TOI callbacks");
            harmony.Patch(PhysicsToiScan.Target, transpiler: new HarmonyMethod(typeof(PhysicsToiScan), nameof(PhysicsToiScan.Transpiler)));
            Assert.Equal(vanilla, SimulateVerified(nested: true));
        }
        finally
        {
            harmony.UnpatchAll(TestId + ".nested");
        }
    }

    private static int _toiDepth, _nestedSteps;

    private static void EnterToi() => _toiDepth++;

    private static Exception? LeaveToi(Exception? __exception)
    {
        _toiDepth--;
        return __exception;
    }

    private static World Populate(int seed, out List<Body> bodies)
    {
        var world = new World(Vector2.Zero);
        var rng = new Random(seed);
        bodies = new List<Body>();
        for (int i = 0; i < 60; i++)
        {
            var body = new Body { BodyType = i % 7 == 0 ? BodyType.Kinematic : BodyType.Dynamic };
            world.Add(body);
            bool bullet = i % 3 == 0;
            body.CreateCircle(bullet ? 0.2f : 1f + (float)rng.NextDouble(), 1f);
            body.IsBullet = bullet || i % 3 == 1;
            body.CCDThresholdVelocity = i % 3 == 1 ? 2f : 0f;
            body.Position = new Vector2((float)rng.NextDouble() * 40 - 20, (float)rng.NextDouble() * 40 - 20);
            body.LinearVelocity = new Vector2((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1) * (bullet ? 400f : 5f);
            body.AngularVelocity = (float)rng.NextDouble() - 0.5f;
            bodies.Add(body);
        }
        return world;
    }

    /// <summary>Full bit snapshot of every body after every step (SHA-256 of the raw floats) plus callback count.</summary>
    private static string Simulate(bool nested = false, bool remote = false)
    {
        World world = Populate(11, out List<Body> bodies);
        World inner = Populate(12, out List<Body> innerBodies);
        int callbacks = 0;
        if (nested)
        {
            world.ContactManager.PreSolve = (Contact contact, ref Halfling.Physics2D.Collision.Manifold _) =>
            {
                if (_toiDepth != 1 || callbacks++ % 3 != 0) return;
                _nestedSteps++;
                inner.Step(1f / 30f);
                contact.Enabled = false;
            };
        }
        if (remote)
        {
            int hits = 0;
            foreach (Body b in bodies)
            {
                b.OnCollision += (_, _, _) =>
                {
                    if (_toiDepth != 1) return true;
                    _remoteChanges++;
                    Body far = bodies[hits++ * 7 % bodies.Count];
                    switch (hits % 4)
                    {
                        case 0: far.LinearVelocity *= -3f; break;
                        case 1: far.IsBullet = !far.IsBullet; break;
                        case 2: far.Awake = !far.Awake; break;
                        default: far.AngularVelocity += 5f; far.CCDThresholdVelocity = far.CCDThresholdVelocity > 0 ? 0f : 1f; break;
                    }
                    return true;
                };
            }
        }
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        byte[] buf = new byte[4];
        void Add(float f) { BitConverter.TryWriteBytes(buf, BitConverter.SingleToInt32Bits(f)); sha.AppendData(buf); }
        for (int step = 0; step < 240; step++)
        {
            world.Step(1f / 30f);
            foreach (Body b in bodies.Concat(innerBodies))
            {
                Add(b.Position.X); Add(b.Position.Y); Add(b.Rotation);
                Add(b.LinearVelocity.X); Add(b.LinearVelocity.Y); Add(b.AngularVelocity);
            }
        }
        return Convert.ToHexString(sha.GetHashAndReset()) + "/" + world.ContactManager.ContactCount + "/" + callbacks;
    }
}