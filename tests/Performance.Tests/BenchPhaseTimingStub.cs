using System;

// ReSharper disable once CheckNamespace (the exemption matches the bench hook type's full name)
namespace Bench;

/// <summary>Stand-in with the identity of E:\DEV\cosmoteer\Bench src/FramePhaseTiming.cs hooks (type name, method names and
/// shapes) for the BeamFrozen foreign-patch exemption tests. The hooks do nothing.</summary>
internal static class FramePhaseTiming
{
    public static void ForPrefix() { }
    public static Exception? ForFinalizer(Exception? __exception) => __exception;
    public static void WaitPrefix() { }
    public static Exception? WaitFinalizer(Exception? __exception) => __exception;
}
