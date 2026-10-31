using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Cosmoteer;
using Halfling;
using Halfling.Application;
using Halfling.Physics2D.Dynamics;
using HarmonyLib;
using V = Halfling.Geometry.Vector2;

namespace Performance;

/// <summary>
/// Limits the lifetime of the shared PhysicsExtensions scratch fields. This does not make same-world/same-tree
/// nested queries safe: World callback holders and DynamicTree traversal stacks remain native and reusable.
/// Patch installation during a frame is unsupported; compatibility is checked before each frame.
/// </summary>
internal static class RaycastScratch
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_RAYCAST_SCRATCH"));
    internal static bool IsRequested(string? value) => value == "1";
    private static int _ready;
    private static bool _attempted;
    private static string? _failureReason;
    private static Director? _director;
    [ThreadStatic] private static int _depth;
    internal static bool Ready => Volatile.Read(ref _ready) != 0;
    internal static string? FailureReason => Volatile.Read(ref _failureReason);
    private static readonly MethodInfo[] Methods = typeof(PhysicsExtensions)
        .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Where(method => method.Name is "TestPointFirst" or "RayCast" or "RayCastFirst" or "RayCastAll" or "RayCastAny")
        .OrderBy(method => method.MetadataToken).ToArray();
    internal static (MethodBase Target, Type Patch)[] Targets => Methods.Select(method => ((MethodBase)method, typeof(RaycastScratch))).ToArray();
    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? Targets : [];
    private static readonly string[] RawPins =
    [
        "DDCD64F76C5BA80DFAB3430DBD8F4B86474F8EA6FE8921C1052FF5B8D5FE2053",
        "DDD096D2112C7B494384080688E2AA017F583C34542DA49F349C8440FB0A2DE7",
        "A083826EA1434E876C1F27FCB0DD29FEDDD3749A9061E5ED63D2DE3FC531E141",
        "F3BA9EDFFD3FD2C01B23C9D328885D882FC34C4725E193D6B92239ACCCE63C34",
        "7F5FDEDC146465F643E9BE6CEE4B61F0BB6BBB5CDC0A94FEB1CE8EE8079892D0",
        "E2363867C8752BB30713297BEBC89E7CC0BB7C89C49CBDE4C5EBEE1A6E3B5C8D",
        "E28E6CA544E60F34D2273BA87AE59704CD122FED20769F8DF0B363ACE7402D93",
        "6155F402F5A9E088652018DFD68432E955D1FEB444B9264AA8A57291BBE03685",
    ];
    private static readonly string[] Pins =
    [
        "002ABC3922AA8AECFC558C6EA1C97592EF0171C3D090B95371CF68029725D79C",
        "9A693D400D6F1514C744592DE4FDD8345904FE0950A42078945F37B15A6F12D2",
        "AAA0C303CF42D54E23B4E96BF2444097D0FF3CBBFE6A83D7D96B0CF1586CE603",
        "1841FB8A1AEA5BCB154B57138F805ABEFA2688161F93EDDBC94C6761B341609A",
        "3288D89D4439DB3304B04FE065BBA86DF7BCEB4B5CFE1993C2E91E172C3A3CD4",
        "373FB96C098C96F78B6FE761CE475EB40867990AAFF50B6870AD1FCCE83A8A5F",
        "D62E48687C5315E57C198F791F5907E9058AC10FA3281DF452DA34355E83CC86",
        "27232319EC0F1BB79748B16F868AB20156888DD98C659A554F282ECE768F6A68",
    ];

    internal static bool Initialize()
    {
        if (!Requested) return true;
        if (_attempted) return Ready;
        _attempted = true;
        try
        {
            Director director = App.Director ?? throw new InvalidOperationException("Raycast scratch requires the native Director");
            if (director.FrameCount != 0) throw new InvalidOperationException("Raycast scratch requires Director.FrameCount == 0");
            ValidateFields();
            if (Methods.Length != Pins.Length) throw new InvalidOperationException("Raycast scratch entry count changed");
            foreach (MethodInfo target in Methods)
            {
                _ = Transpiler(PatchProcessor.GetOriginalInstructions(target), target);
                if (Patcher.FindR2RInliners(target).Count != 0) throw new NotSupportedException("Raycast scratch target has precompiled inliners: " + target);
                if (!Patcher.TryDisableInlining(target, out string? error)) throw new NotSupportedException(error);
            }
            ValidateHookCoverage();
            if (!Compatible()) throw new NotSupportedException("Foreign patch on PhysicsExtensions scratch dependency");
            _director = director;
            director.FrameStarting += OnFrameStarting;
            Volatile.Write(ref _ready, 1);
            Log.Info("Raycast scratch prototype ready: eight helper scopes; same-tree nesting remains unsupported");
            return true;
        }
        catch (Exception ex) { Reject(ex.Message); return false; }
    }

    private static bool HasHook(IEnumerable<Patch> patches, string name) => patches.Any(patch =>
        patch.owner == Patcher.HarmonyId && patch.PatchMethod.DeclaringType == typeof(RaycastScratch) && patch.PatchMethod.Name == name);
    private static void ValidateHookCoverage()
    {
        foreach (MethodInfo target in Methods)
        {
            Patches? patches = Harmony.GetPatchInfo(target);
            if (patches == null || !HasHook(patches.Prefixes, nameof(Prefix)) || !HasHook(patches.Transpilers, nameof(Transpiler)) || !HasHook(patches.Finalizers, nameof(Finalizer)))
                throw new InvalidOperationException("Raycast scratch hook coverage missing: " + target);
        }
    }

    private static bool Compatible()
    {
        foreach (MethodBase method in Harmony.GetAllPatchedMethods())
        {
            bool dependency = false;
            for (Type? type = method.DeclaringType; type != null; type = type.DeclaringType)
                if (type == typeof(PhysicsExtensions)) { dependency = true; break; }
            if (!dependency) continue;
            Patches? patches = Harmony.GetPatchInfo(method);
            if (patches == null || patches.Owners.Any(owner => owner != Patcher.HarmonyId)) return false;
        }
        return true;
    }
    internal static void CheckCompatibility()
    {
        if (!Ready) return;
        try { ValidateHookCoverage(); if (!Compatible()) Reject("Foreign patch on PhysicsExtensions scratch dependency"); }
        catch (Exception ex) { Reject(ex.Message); }
    }
    private static void OnFrameStarting(object? sender, EventArgs e) => CheckCompatibility();
    private static void Reject(string reason)
    {
        Interlocked.CompareExchange(ref _failureReason, reason, null);
        Disable();
        Log.Info("Raycast scratch prototype disabled: " + FailureReason);
    }
    internal static void Disable()
    {
        Volatile.Write(ref _ready, 0);
        if (_director == null) return;
        _director.FrameStarting -= OnFrameStarting;
        _director = null;
        // Active frames, including frames on other threads, still own their finalizer cleanup.
    }

    private static void ValidateFields()
    {
        (string Name, Type Type)[] expected =
        [
            ("s_condition", typeof(Func<Fixture, V, bool>)), ("s_dataCondition", typeof(Func<Fixture, V, (bool, object?)>)),
            ("s_hitFixture", typeof(Fixture)), ("s_hitData", typeof(object)), ("s_hitPoint", typeof(V)), ("s_hitNormal", typeof(V)),
            ("s_hits", typeof(IDictionary<Fixture, (V, V)>)),
        ];
        FieldInfo[] fields = typeof(PhysicsExtensions).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(field => field.IsDefined(typeof(ThreadStaticAttribute), false)).ToArray();
        if (fields.Length != expected.Length || expected.Any(item => !fields.Any(field => field.Name == item.Name && field.FieldType == item.Type && !field.IsInitOnly && !field.IsLiteral)))
            throw new InvalidOperationException("PhysicsExtensions ThreadStatic field shape changed");
    }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        int index = Array.FindIndex(Methods, method => method == __originalMethod);
        if (index < 0 || index >= Pins.Length || __originalMethod.MetadataToken != 0x06000138 + index ||
            __originalMethod.Module.ModuleVersionId != new Guid("fd7c0bba-64d3-40b4-a571-bf8b34eb4277") ||
            Convert.ToHexString(SHA256.HashData(__originalMethod.GetMethodBody()!.GetILAsByteArray()!)) != RawPins[index])
            throw new InvalidOperationException("Raycast scratch native IL/MVID changed");
        return Patcher.RequireIl(instructions, Pins[index], "PhysicsExtensions." + __originalMethod.Name);
    }

    public readonly struct Scope
    {
        internal readonly bool Entered;
        internal readonly int PreviousDepth;
        internal readonly Func<Fixture, V, bool>? Condition;
        internal readonly Func<Fixture, V, (bool, object?)>? DataCondition;
        internal readonly Fixture? HitFixture;
        internal readonly object? HitData;
        internal readonly V HitPoint;
        internal readonly V HitNormal;
        internal readonly IDictionary<Fixture, (V, V)>? Hits;
        internal Scope(int previousDepth)
        {
            this = default;
            Entered = true; PreviousDepth = previousDepth;
            if (previousDepth == 0) return; // Never resurrect arbitrary stale top-level scratch.
            Condition = PhysicsExtensions.s_condition; DataCondition = PhysicsExtensions.s_dataCondition;
            HitFixture = PhysicsExtensions.s_hitFixture; HitData = PhysicsExtensions.s_hitData;
            HitPoint = PhysicsExtensions.s_hitPoint; HitNormal = PhysicsExtensions.s_hitNormal; Hits = PhysicsExtensions.s_hits;
        }
    }
    public static void Prefix(out Scope __state)
    {
        __state = default;
        if (!Ready && _depth == 0) return;
        __state = new Scope(_depth);
        _depth++;
        Clear();
    }
    public static Exception? Finalizer(Scope __state, Exception? __exception)
    {
        if (!__state.Entered) return __exception;
        if (__state.PreviousDepth == 0) Clear();
        else
        {
            PhysicsExtensions.s_condition = __state.Condition; PhysicsExtensions.s_dataCondition = __state.DataCondition;
            PhysicsExtensions.s_hitFixture = __state.HitFixture; PhysicsExtensions.s_hitData = __state.HitData;
            PhysicsExtensions.s_hitPoint = __state.HitPoint; PhysicsExtensions.s_hitNormal = __state.HitNormal; PhysicsExtensions.s_hits = __state.Hits;
        }
        _depth = __state.PreviousDepth;
        return __exception;
    }
    private static void Clear()
    {
        PhysicsExtensions.s_condition = null; PhysicsExtensions.s_dataCondition = null;
        PhysicsExtensions.s_hitFixture = null; PhysicsExtensions.s_hitData = null; PhysicsExtensions.s_hits = null;
        PhysicsExtensions.s_hitPoint = default; PhysicsExtensions.s_hitNormal = default;
    }
}
