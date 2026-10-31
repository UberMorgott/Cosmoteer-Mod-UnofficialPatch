using System.Threading;

namespace Performance;

internal sealed class AtlasOwnerGate(bool dedicated)
{
    private readonly Lock? _dedicated = dedicated ? new Lock() : null;
    private readonly object? _monitor = dedicated ? null : new object();
    internal static bool Select(string? mode, string? flag) => mode == "3" && flag == "1";
    internal bool IsHeldByCurrentThread => _dedicated?.IsHeldByCurrentThread ?? Monitor.IsEntered(_monitor!);
    // Never use Monitor on the Lock instance: the two synchronization mechanisms are not interchangeable.
    internal void Enter()
    {
        if (_dedicated != null) _dedicated.Enter(); else Monitor.Enter(_monitor!);
    }
    internal void Exit()
    {
        if (_dedicated != null) _dedicated.Exit(); else Monitor.Exit(_monitor!);
    }
}
