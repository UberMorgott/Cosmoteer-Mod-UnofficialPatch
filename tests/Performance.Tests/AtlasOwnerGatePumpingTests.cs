using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Halfling.Application;
using Xunit;
using ThreadState = System.Threading.ThreadState;

namespace Performance.Tests;

public sealed class AtlasOwnerGatePumpingTests
{
    [Fact]
    public async Task NativeStaBlockedEnterHasMatchingSentMessageReentrancy()
    {
        if (Environment.GetEnvironmentVariable("ATLAS_LOCK_PUMP_CHILD") == "1")
        {
            bool monitor = Probe(false), dedicated = Probe(true);
            Console.WriteLine($"Atlas owner STA sent-message callback while holder owns gate: Monitor={monitor}, Lock={dedicated}");
            Assert.Equal(monitor, dedicated);
            return;
        }
        var start = new ProcessStartInfo(Path.ChangeExtension(typeof(AtlasOwnerGatePumpingTests).Assembly.Location, ".exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-method"); start.ArgumentList.Add("Performance.Tests.AtlasOwnerGatePumpingTests.NativeStaBlockedEnterHasMatchingSentMessageReentrancy");
        start.ArgumentList.Add("-noLogo"); start.Environment["ATLAS_LOCK_PUMP_CHILD"] = "1";
        using Process child = Process.Start(start)!;
        Task<string> stdout = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(20_000);
        try
        {
            await child.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
            string output = await stdout.ConfigureAwait(true) + await stderr.ConfigureAwait(true);
            Console.WriteLine(output); Assert.True(child.ExitCode == 0, output);
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(true); } }
    }
    private static bool Probe(bool dedicated)
    {
        var gate = new AtlasOwnerGate(dedicated);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var entering = new ManualResetEventSlim();
        // These fixed borrowed references outlive both workers: finally joins them before the using scopes end.
        var witness = new WaitWitness(held, release, entering);
        var holder = new Thread(() =>
        {
            try { gate.Enter(); try { witness.Held.Set(); witness.Release.Wait(); } finally { gate.Exit(); } }
            catch (Exception error) { witness.HolderError = error; }
        });
        var sta = new Thread(() =>
        {
            try
            {
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                var context = new QueueingSyncContext(); SynchronizationContext.SetSynchronizationContext(context);
                int posted = 0; context.Post(() => posted++);
                using var window = new MessageWindow(() =>
                {
                    if (Volatile.Read(ref witness.ReleaseIssued) == 0)
                    {
                        Assert.False(gate.IsHeldByCurrentThread);
                        Interlocked.Exchange(ref witness.ObservedBeforeRelease, 1);
                    }
                    // Exercise reentrant acquisition from the sent-message callback while outer Enter is suspended.
                    gate.Enter(); try { Assert.True(gate.IsHeldByCurrentThread); gate.Enter(); gate.Exit(); } finally { gate.Exit(); }
                });
                witness.Handle = window.Handle; witness.Entering.Set(); gate.Enter();
                try { Assert.Equal(0, posted); context.HandleQueue(); Assert.Equal(1, posted); }
                finally { gate.Exit(); }
                Assert.Null(window.CallbackError);
            }
            catch (Exception error) { witness.StaError = error; }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        });
        sta.SetApartmentState(ApartmentState.STA);
        try
        {
            holder.Start(); Assert.True(held.Wait(5000, TestContext.Current.CancellationToken));
            sta.Start(); Assert.True(entering.Wait(5000, TestContext.Current.CancellationToken));
            Assert.True(SpinWait.SpinUntil(() => sta.ThreadState.HasFlag(ThreadState.WaitSleepJoin), 5000));
            // The invisible HWND belongs only to the new STA test thread; no user window/focus is touched.
            MessageWindow.SendBounded(witness.Handle);
        }
        finally
        {
            Volatile.Write(ref witness.ReleaseIssued, 1); release.Set();
            if (!sta.ThreadState.HasFlag(ThreadState.Unstarted)) Assert.True(sta.Join(5000));
            if (!holder.ThreadState.HasFlag(ThreadState.Unstarted)) Assert.True(holder.Join(5000));
        }
        Assert.Null(witness.HolderError); Assert.Null(witness.StaError);
        return witness.ObservedBeforeRelease != 0;
    }
    private sealed class WaitWitness(ManualResetEventSlim held, ManualResetEventSlim release, ManualResetEventSlim entering)
    {
        internal readonly ManualResetEventSlim Held = held, Release = release, Entering = entering;
        internal Exception? HolderError, StaError;
        internal int ReleaseIssued, ObservedBeforeRelease;
        internal nint Handle;
    }
    private sealed class MessageWindow : IDisposable
    {
        private const uint Message = 0x805B;
        private readonly string _name = "PerformanceAtlasLock_" + Guid.NewGuid().ToString("N");
        private readonly WindowProcedure _procedure;
        private readonly nint _instance;
        internal nint Handle { get; private set; }
        internal Exception? CallbackError { get; private set; }
        internal MessageWindow(Action callback)
        {
            _procedure = (window, message, wparam, lparam) =>
            {
                if (message == Message)
                {
                    try { callback(); }
                    catch (Exception error) { CallbackError = error; }
                    return 0;
                }
                return DefWindowProc(window, message, wparam, lparam);
            };
            _instance = GetModuleHandle(null);
            var description = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(_procedure), Instance = _instance, ClassName = _name };
            if (RegisterClassEx(ref description) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            Handle = CreateWindowEx(0, _name, "", 0, 0, 0, 0, 0, -3, 0, _instance, 0);
            if (Handle == 0) { int error = Marshal.GetLastPInvokeError(); UnregisterClass(_name, _instance); throw new Win32Exception(error); }
        }
        internal static void SendBounded(nint handle) => _ = SendMessageTimeout(handle, Message, 0, 0, 3, 500, out _);
        public void Dispose()
        {
            try { Assert.True(DestroyWindow(Handle)); }
            finally { Assert.True(UnregisterClass(_name, _instance)); GC.KeepAlive(_procedure); }
        }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate nint WindowProcedure(nint window, uint message, nuint wparam, nint lparam);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            internal uint Size, Style;
            internal nint Procedure;
            internal int ClassExtra, WindowExtra;
            internal nint Instance, Icon, Cursor, Background;
            internal string? MenuName;
            internal string ClassName;
            internal nint SmallIcon;
        }
        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint GetModuleHandle(string? name);
        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern ushort RegisterClassEx(ref WindowClass description);
        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint CreateWindowEx(uint extended, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint DefWindowProc(nint window, uint message, nuint wparam, nint lparam);
        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterClass(string name, nint instance);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint SendMessageTimeout(nint window, uint message, nuint wparam, nint lparam, uint flags, uint timeout, out nuint result);
    }
}
