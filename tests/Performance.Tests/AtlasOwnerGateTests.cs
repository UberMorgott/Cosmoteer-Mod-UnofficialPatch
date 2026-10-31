using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Halfling.Application;
using Xunit;

namespace Performance.Tests;

public sealed class AtlasOwnerGateTests
{
    [Theory]
    [InlineData("3", "1", true)]
    [InlineData("2", "1", false)]
    [InlineData("1", "1", false)]
    [InlineData(null, "1", false)]
    [InlineData("3", null, false)]
    [InlineData("3", "true", false)]
    public void DedicatedBackendRequiresBothExplicitFlags(string? mode, string? flag, bool expected) =>
        Assert.Equal(expected, AtlasOwnerGate.Select(mode, flag));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SuppressMessage("ReSharper", "AccessToDisposedClosure", Justification = "Both native threads are released and joined in finally before the captured synchronization events are disposed.")]
    public void ActualStaAndNativeQueueingContextDoNotDispatchPostedCallbacksInsideContendedEnter(bool dedicated)
    {
        var gate = new AtlasOwnerGate(dedicated);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var entering = new ManualResetEventSlim();
        Exception? holderError = null, staError = null;
        var callbacks = new StrongBox<int>();
        var holder = new Thread(() =>
        {
            try { gate.Enter(); held.Set(); release.Wait(); gate.Exit(); }
            catch (Exception error) { holderError = error; }
        });
        var sta = new Thread(() =>
        {
            try
            {
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                var context = new QueueingSyncContext();
                Assert.Equal(typeof(SynchronizationContext), typeof(QueueingSyncContext).GetMethod(nameof(SynchronizationContext.Wait))!.DeclaringType);
                Assert.False(context.IsWaitNotificationRequired());
                SynchronizationContext.SetSynchronizationContext(context);
                context.Post(() =>
                {
                    Assert.True(gate.IsHeldByCurrentThread);
                    gate.Enter(); gate.Enter(); gate.Exit(); gate.Exit();
                    Interlocked.Increment(ref callbacks.Value);
                });
                entering.Set(); gate.Enter();
                try
                {
                    Assert.True(gate.IsHeldByCurrentThread); Assert.Equal(0, Volatile.Read(ref callbacks.Value));
                    context.HandleQueue(); Assert.Equal(1, Volatile.Read(ref callbacks.Value));
                    Assert.True(gate.IsHeldByCurrentThread);
                }
                finally { gate.Exit(); SynchronizationContext.SetSynchronizationContext(null); }
                Assert.False(gate.IsHeldByCurrentThread);
            }
            catch (Exception error) { staError = error; }
        });
        sta.SetApartmentState(ApartmentState.STA);
        try
        {
            holder.Start(); Assert.True(held.Wait(5000, TestContext.Current.CancellationToken));
            sta.Start(); Assert.True(entering.Wait(5000, TestContext.Current.CancellationToken));
            Assert.True(SpinWait.SpinUntil(() => sta.ThreadState.HasFlag(ThreadState.WaitSleepJoin), 5000));
            Assert.Equal(0, Volatile.Read(ref callbacks.Value));
        }
        finally
        {
            release.Set();
            if (!sta.ThreadState.HasFlag(ThreadState.Unstarted)) Assert.True(sta.Join(5000));
            if (!holder.ThreadState.HasFlag(ThreadState.Unstarted)) Assert.True(holder.Join(5000));
        }
        Assert.Null(holderError); Assert.Null(staError); Assert.Equal(1, callbacks.Value);
    }
}
