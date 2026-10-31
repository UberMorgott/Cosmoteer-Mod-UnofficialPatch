using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Cosmoteer.Ships.Rendering;
using Halfling;
using Halfling.Graphics;
using HarmonyLib;
using SharpDX;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Performance;

/// <summary>Startup-only, opt-in exact dirty regions on the stock Static atlas backend. Unknown state stays native/full.</summary>
internal static class AtlasPartialUploads
{
    private static readonly string? RequestedMode = Environment.GetEnvironmentVariable("PERFORMANCE_ATLAS_STATIC");
    internal static readonly bool Requested = IsRequested(RequestedMode);
    private static readonly bool SingleRange = RequestedMode == "3";
    internal static readonly bool DedicatedLockRequested = AtlasOwnerGate.Select(RequestedMode, Environment.GetEnvironmentVariable("PERFORMANCE_ATLAS_LOCK"));
    private static long _dedicatedLockOwners, _monitorLockOwners;
    internal static long DedicatedLockOwners => Interlocked.Read(ref _dedicatedLockOwners);
    internal static long MonitorLockOwners => Interlocked.Read(ref _monitorLockOwners);
    internal static bool IsRequested(string? value) => value is "2" or "3";
    private static int _ready, _cleanupPending, _cleanupAll, _synchronizing;
    [ThreadStatic] private static int _heldGates;
    private static string? _failureReason;
    internal static bool Ready { get => Volatile.Read(ref _ready) != 0; private set => Volatile.Write(ref _ready, value ? 1 : 0); }
    internal static string? FailureReason => Volatile.Read(ref _failureReason);
    private static long _fullUploads, _partialUploads, _uploadedBytes, _fullEquivalentBytes, _uploadCalls;
    internal static long FullUploads => Interlocked.Read(ref _fullUploads);
    internal static long PartialUploads => Interlocked.Read(ref _partialUploads);
    internal static long UploadedBytes => Interlocked.Read(ref _uploadedBytes);
    internal static long FullEquivalentBytes => Interlocked.Read(ref _fullEquivalentBytes);
    internal static long UploadCalls => Interlocked.Read(ref _uploadCalls);
    internal static readonly MethodInfo UploadTarget = AccessTools.Method(typeof(DeviceContext), "UpdateSubresource", [typeof(DataBox), typeof(Resource), typeof(int)]);
    internal static readonly MethodInfo UploadCore = AccessTools.Method(typeof(DeviceContext), "UpdateSubresource", [typeof(Resource), typeof(int), typeof(ResourceRegion?), typeof(IntPtr), typeof(int), typeof(int)]);
    internal static readonly (MethodBase Target, Type Patch)[] WriterPatches =
    [
        (AtlasWriteCoverage.Targets[0].Target, typeof(DataWriteHook)),
        (AtlasWriteCoverage.Targets[1].Target, typeof(CreateHook)),
        (AtlasWriteCoverage.Targets[2].Target, typeof(QuadRemoveHook)),
        (AtlasWriteCoverage.Targets[3].Target, typeof(QuadRemoveHook)),
        (AtlasWriteCoverage.Targets[4].Target, typeof(ClearHook)),
        (AtlasWriteCoverage.Targets[5].Target, typeof(RetireHook)),
    ];
    internal static readonly (MethodBase Target, Type Patch)[] DrawPatches = AtlasWriteCoverage.DrawTargets.Select(t => ((MethodBase)t.Target, typeof(DrawHook))).ToArray();
    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? [.. WriterPatches, .. DrawPatches, (UploadTarget, typeof(UploadHook))] : [];
    private static readonly ConditionalWeakTable<AtlasQuadManager, Owner> Owners = new();
    private static readonly ConditionalWeakTable<GraphicsArray<AtlasQuad>, UploadState> Arrays = new();
    private static readonly ConditionalWeakTable<Buffer, UploadState> Buffers = new();
    private static readonly List<WeakReference<UploadState>> States = new();
    private static readonly object RegistryGate = new();
    private static WeakReference<UploadState>[] _snapshot = [];
    private static readonly HashSet<(Module, int)> Dependencies = new();
    private static NativeAccess? _native;
    private static WeakReference<DeviceContext>? _context;
    private static int _thread;
    private static bool _initialized, _attached, _logged, _failureLogged;

    internal static bool Initialize()
    {
        if (!Requested) return true;
        if (_initialized) return Ready;
        _initialized = true;
        _thread = Environment.CurrentManagedThreadId;
        try
        {
            if (!AtlasWriteCoverage.TryValidateStartup(App.Director.FrameCount, App.Graphics, out string? error) ||
                !AtlasWriteCoverage.TryPrepare(out error)) throw new InvalidOperationException(error);
            ConfigureBackend(App.Graphics.GetType().Assembly);
            DeviceContext context = (DeviceContext)AccessTools.Property(App.Graphics.GetType(), "Context").GetValue(App.Graphics)!;
            if (context.TypeInfo != DeviceContextType.Immediate) throw new NotSupportedException("Atlas partial uploads require an immediate context");
            _context = new WeakReference<DeviceContext>(context);
            _thread = Environment.CurrentManagedThreadId;
            RequireUploadIl(UploadTarget, PatchProcessor.GetOriginalInstructions(UploadTarget));
            RequireUploadIl(UploadCore, PatchProcessor.GetOriginalInstructions(UploadCore));
            foreach (MethodInfo method in new[] { UploadTarget, UploadCore })
                if (Patcher.FindR2RInliners(method).Count != 0 || !AtlasWriteCoverage.TryDisableInlining(method, out error))
                    throw new NotSupportedException(error ?? "Native upload has precompiled inliners");
            if (!Compatible()) throw new NotSupportedException("Foreign atlas/upload patch at initialization");
            App.Director.FrameStarting += OnFrameStarting;
            _attached = true;
            Volatile.Write(ref _synchronizing, 1);
            Ready = true;
            return true;
        }
        catch (Exception ex)
        {
            Stop(ex.Message);
            return false;
        }
    }

    internal static void ConfigureBackend(Assembly backend)
    {
        AtlasUploadMode.RequireBackendBinary(backend.Location);
        _native = new NativeAccess(backend);
        Dependencies.Clear();
        Type[] types = [typeof(AtlasQuadManager), AtlasWriteCoverage.InternalQuadType, typeof(GraphicsList<AtlasQuad>), _native.Type, _native.Type.BaseType!, backend.GetType("Halfling.Graphics.D3D11.D3D11GraphicsManager", true)!];
        foreach (Type type in types)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (MethodBase method in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)))
                Dependencies.Add((method.Module, method.MetadataToken));
        }
        foreach (MethodInfo method in typeof(DeviceContext).GetMethods().Where(m => m.Name.StartsWith("Draw", StringComparison.Ordinal) || m.Name is "UpdateSubresource" or "MapSubresource" or "UnmapSubresource"))
            Dependencies.Add((method.Module, method.MetadataToken));
        foreach (Type type in new[] { typeof(Buffer), typeof(DeviceContext) })
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name is "get_Description" or "GetDescription" or "get_TypeInfo" or "GetTypeInfo" or "get_NativePointer"))
                Dependencies.Add((method.Module, method.MetadataToken));
    }

    internal static List<CodeInstruction> RequireUploadIl(MethodBase method, IEnumerable<CodeInstruction> instructions) =>
        Patcher.RequireIl(instructions, method == UploadTarget ? "88EE21C69FD55D0BB0ED3EB7653FD0888BFCFBB1E41C82BEDA0E40A0F204A7CA" : method == UploadCore ? "5A53B181524A0EEDBE6C748F0BFC9983FA8B78ACC6E6C5C818A0E537290C5A64" : throw new InvalidOperationException("Unknown atlas upload method"), method.ToString()!);

    private static bool Compatible()
    {
        foreach (MethodBase method in Harmony.GetAllPatchedMethods())
        {
            if (!Dependencies.Contains((method.Module, method.MetadataToken))) continue;
            Patches? patches = Harmony.GetPatchInfo(method);
            if (patches == null) return false;
            foreach (string owner in patches.Owners)
                if (owner != Patcher.HarmonyId) return false;
        }
        return true;
    }

    private static void OnFrameStarting(object? sender, EventArgs e)
    {
        try
        {
            if (Environment.CurrentManagedThreadId != _thread) { Stop("Unexpected graphics thread"); return; }
            if (Volatile.Read(ref _cleanupPending) != 0) { Cleanup(); return; }
            if (Ready && !Compatible()) Stop("Foreign patch on atlas mutation/draw/upload dependency");
            if (Ready) Recover();
            if (Volatile.Read(ref _cleanupPending) != 0) Cleanup();
        }
        catch (Exception ex) { Stop(ex.Message); }
    }

    internal static void Disable()
    {
        bool worker = Environment.CurrentManagedThreadId != _thread;
        bool deferred = worker || _heldGates != 0;
        if (worker) Volatile.Write(ref _cleanupAll, 1);
        Ready = false;
        Volatile.Write(ref _cleanupPending, 1);
        if (deferred) return;
        Cleanup();
    }

    private static void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleanupPending, 0) == 0) return;
        bool all = Interlocked.Exchange(ref _cleanupAll, 0) != 0;
        (WeakReference<UploadState>[] snapshot, int count) = Snapshot();
        for (int i = 0; i < count; i++)
        {
            if (!snapshot[i].TryGetTarget(out UploadState? state)) continue;
            using GateLease lease = new(state.Owner);
            bool pending = all || state.Dirty.HasPendingChanges || state.Generation != state.Owner.Generation || state.UploadDepth != 0;
            state.Dirty.Fail(); state.NeedsRecovery |= pending;
        }
        Recover();
        Detach();
        if (!_failureLogged && FailureReason is { } reason)
        {
            _failureLogged = true;
            Log.Info("Partial atlas uploads disabled: " + reason);
        }
    }

    private static void Detach()
    {
        if (!_attached) return;
        App.Director.FrameStarting -= OnFrameStarting;
        _attached = false;
    }

    private static void Stop(string reason)
    {
        Interlocked.CompareExchange(ref _failureReason, reason, null);
        Disable();
    }

    private static bool GraphicsActive()
    {
        if (!Ready) return false;
        if (Environment.CurrentManagedThreadId == _thread) return true;
        string reason = "Atlas upload on another thread";
        if (FailureReason == null)
            reason += $" (current={Environment.CurrentManagedThreadId}, graphics={_thread})" + Environment.NewLine + new StackTrace(1, false);
        Stop(reason);
        return false;
    }

    private static void Recover()
    {
        (WeakReference<UploadState>[] snapshot, int count) = Snapshot();
        for (int i = 0; i < count; i++)
        {
            if (!snapshot[i].TryGetTarget(out UploadState? state)) continue;
            using GateLease lease = new(state.Owner);
            if (!state.NeedsRecovery || !state.Array.TryGetTarget(out GraphicsArray<AtlasQuad>? array) || _native == null || _native.Disposed(array)) continue;
            // Native UpdateDirtyData may clear its flag after our wrapper returned. Restore it only at the next frame.
            try
            {
                unsafe { _ = array.GetUnsafeTempPointer(readOnly: false); }
                state.NeedsRecovery = false;
            }
            catch (Exception ex)
            {
                Ready = false;
                Volatile.Write(ref _cleanupPending, 1);
                Interlocked.CompareExchange(ref _failureReason, "Native dirty recovery failed: " + ex.Message, null);
            }
        }
    }

    private static (WeakReference<UploadState>[], int) Snapshot()
    {
        lock (RegistryGate)
        {
            for (int i = States.Count - 1; i >= 0; i--)
                if (!States[i].TryGetTarget(out _)) { States[i] = States[^1]; States.RemoveAt(States.Count - 1); }
            if (_snapshot.Length < States.Count) _snapshot = new WeakReference<UploadState>[Math.Max(States.Count, _snapshot.Length * 2)];
            States.CopyTo(_snapshot);
            return (_snapshot, States.Count);
        }
    }

    private readonly struct GateLease : IDisposable
    {
        private readonly Owner _owner;
        internal GateLease(Owner owner) { _owner = owner; owner.Gate.Enter(); _heldGates++; }
        public void Dispose() { _heldGates--; _owner.Gate.Exit(); }
    }

    internal sealed class Owner
    {
        internal readonly WeakReference<AtlasQuadManager> Manager;
        internal readonly AtlasOwnerGate Gate;
        internal long Generation;
        internal int Depth, DrawDepth, DrawThread;
        internal bool Retired;
        internal Owner(AtlasQuadManager manager) : this(manager, DedicatedLockRequested) { }
        internal Owner(AtlasQuadManager manager, bool dedicated)
        {
            Manager = new(manager); Gate = new(dedicated);
            if (dedicated)
            {
                if (Interlocked.Increment(ref _dedicatedLockOwners) == 1) Log.Info("Atlas owner synchronization active: System.Threading.Lock (immutable owner backend)");
            }
            else Interlocked.Increment(ref _monitorLockOwners);
        }
    }

    internal sealed class UploadState(Owner owner, GraphicsArray<AtlasQuad> array, int capacity)
    {
        internal readonly Owner Owner = owner;
        internal readonly WeakReference<GraphicsArray<AtlasQuad>> Array = new(array);
        internal readonly AtlasDirtyRanges Dirty = new(capacity);
        internal WeakReference<Buffer>? Buffer;
        internal IntPtr Source;
        internal long Generation = owner.Generation, Identity;
        internal int UploadDepth;
        internal bool NeedsRecovery;

        internal UploadAttempt Begin() => Begin(SingleRange);

        internal UploadAttempt Begin(bool singleRange)
        {
            UploadDepth++;
            if (UploadDepth != 1) { FailPending(); return new UploadAttempt(this, default, false); }
            if (Generation != Owner.Generation || Owner.Depth != 0 || !Dirty.HasPendingChanges) Dirty.RequireFull();
            return new UploadAttempt(this, Dirty.Plan(singleRange), true);
        }

        internal void FailPending() { Dirty.RequireFull(); NeedsRecovery = true; }
        internal void Fail() { Dirty.Fail(); NeedsRecovery = true; }
    }

    internal readonly struct UploadAttempt(UploadState state, AtlasUploadPlan plan, bool owns, bool requireCommit = false)
    {
        internal UploadState? State { get; } = state;
        internal AtlasUploadPlan Plan { get; } = plan;
        private long Generation { get; } = state.Owner.Generation;
        private long Identity { get; } = state.Identity;
        private IntPtr Source { get; } = state.Source;
        private int Capacity { get; } = state.Dirty.Capacity;
        private bool Owns { get; } = owns;
        internal bool RequiresCommit { get; private init; } = requireCommit;
        internal long SubmittedBytes { get; private init; }
        internal int SubmittedCalls { get; private init; }
        internal bool OwnsPlan => Owns;
        internal bool SameBinding => State != null && Identity == State.Identity && Source == State.Source && Capacity == State.Dirty.Capacity;
        internal bool CanContinue => State != null && Owns && Generation == State.Owner.Generation && Identity == State.Identity &&
            State.Owner.Depth == 0 && !State.Owner.Retired && State.Dirty.Version == Plan.Version;
        internal UploadAttempt RequireCommit() => this with { RequiresCommit = true };
        internal UploadAttempt AddSubmitted(long bytes, int calls = 1) => this with { SubmittedBytes = SubmittedBytes + bytes, SubmittedCalls = SubmittedCalls + calls };
        internal bool NativeFull => !Owns || Plan.Full;
        internal long CapacityBytes => (long)Capacity * Unsafe.SizeOf<AtlasQuad>();

        internal bool Finish(bool success)
        {
            if (State == null) return false;
            try
            {
                if (Owns && success && State.UploadDepth == 1 && State.Owner.Depth == 0 && !State.Owner.Retired &&
                    Generation == State.Owner.Generation && Identity == State.Identity && State.Dirty.Complete(Plan.Version))
                {
                    State.NeedsRecovery = false;
                    return true;
                }
                if (Owns) State.Fail(); else State.FailPending();
                return false;
            }
            finally { State.UploadDepth--; }
        }
    }

    private static Owner OwnerOf(AtlasQuadManager manager) => Owners.GetValue(manager, static m => new Owner(m));
    internal static UploadState? StateOf(GraphicsArray<AtlasQuad> array) => Arrays.TryGetValue(array, out UploadState? state) ? state : null;

    private static UploadState? Bind(Owner owner, int subIndex)
    {
        if (_native == null || owner.Retired || !owner.Manager.TryGetTarget(out AtlasQuadManager? manager) || (uint)subIndex >= (uint)manager._lists.Length) return null;
        GraphicsArray<AtlasQuad>? array = manager._lists[subIndex]?.InternalArray;
        if (array == null || array.GetType() != _native.Type || _native.Disposed(array)) return null;
        int capacity = _native.Size(array);
        if (capacity <= 0) return null;
        if (!Arrays.TryGetValue(array, out UploadState? state))
        {
            state = new UploadState(owner, array, capacity);
            Arrays.Add(array, state);
            lock (RegistryGate) States.Add(new WeakReference<UploadState>(state));
        }
        if (!ReferenceEquals(state.Owner, owner)) return null;
        Buffer? buffer = _native.Buffer(array);
        IntPtr source = _native.Source(array);
        bool sameBuffer = buffer == null ? state.Buffer == null : state.Buffer != null && state.Buffer.TryGetTarget(out Buffer? old) && ReferenceEquals(old, buffer);
        if (state.Dirty.Capacity != capacity || state.Source != source || !sameBuffer)
        {
            state.Dirty.Resize(capacity);
            state.Identity++;
            state.Source = source;
            state.Buffer = buffer == null ? null : new WeakReference<Buffer>(buffer);
            if (buffer != null) Buffers.AddOrUpdate(buffer, state);
        }
        if (state.Generation != owner.Generation)
        {
            state.Generation = owner.Generation;
            state.Dirty.RequireFull();
        }
        if (_native.Dirty(array) && !state.Dirty.HasPendingChanges) state.Dirty.RequireFull();
        if (state.UploadDepth != 0) state.FailPending();
        return state;
    }

    private static void Mark(object quad, Owner owner)
    {
        if (!Ready) return;
        try
        {
            var native = (AtlasQuadManager.InternalManagedAtlasQuad)quad;
            if (native._pm == null || !owner.Manager.TryGetTarget(out AtlasQuadManager? manager) || !ReferenceEquals(native._pm, manager)) return;
            UploadState? state = Bind(owner, native._subIndex);
            if (state != null && (uint)native._index < (uint)state.Dirty.Capacity) state.Dirty.Mark(native._index);
        }
        catch (Exception ex) { Stop(ex.Message); }
    }

    internal enum ScopeKind { Write, Structural, Draw }
    internal readonly record struct Mutation(Owner? Owner, int SubIndex, ScopeKind Kind);

    private static Mutation BeginMutation(AtlasQuadManager? manager, int subIndex = -1, bool retire = false, ScopeKind kind = ScopeKind.Structural)
    {
        if (manager == null || (Volatile.Read(ref _synchronizing) == 0 && !Ready)) return default;
        if (Ready) Volatile.Write(ref _synchronizing, 1);
        Owner owner = OwnerOf(manager);
        // Synchronization survives Ready=false: a new writer must not bypass an already-running draw.
        owner.Gate.Enter();
        _heldGates++;
        if (kind == ScopeKind.Draw)
        {
            owner.DrawDepth++;
            owner.DrawThread = Environment.CurrentManagedThreadId;
        }
        else if (kind == ScopeKind.Structural)
        {
            owner.Depth++;
            owner.Generation++;
            owner.Retired |= retire;
        }
        return new Mutation(owner, subIndex, kind);
    }

    private static Exception? EndMutation(Exception? error, Mutation mutation)
    {
        if (mutation.Owner == null) return error;
        try
        {
            if (mutation.Kind == ScopeKind.Structural)
            {
                mutation.Owner.Generation++;
                mutation.Owner.Depth--;
                if (Ready && error == null && mutation.SubIndex >= 0) Bind(mutation.Owner, mutation.SubIndex);
            }
            else if (mutation.Kind == ScopeKind.Draw && --mutation.Owner.DrawDepth == 0) mutation.Owner.DrawThread = 0;
        }
        catch (Exception ex) { Stop(ex.Message); }
        finally { _heldGates--; mutation.Owner.Gate.Exit(); }
        return error;
    }

    internal static bool MatchesSource(DataBox source, IntPtr expectedSource, int capacity, BufferDescription description, int subresource)
    {
        long bytes = (long)capacity * Unsafe.SizeOf<AtlasQuad>();
        return capacity > 0 && bytes <= int.MaxValue && subresource == 0 && expectedSource != IntPtr.Zero && source.DataPointer == expectedSource &&
            source.RowPitch == bytes && source.SlicePitch == bytes && description.SizeInBytes == bytes && description.Usage == ResourceUsage.Default &&
            description.BindFlags == BindFlags.VertexBuffer && description.CpuAccessFlags == CpuAccessFlags.None &&
            description.OptionFlags == ResourceOptionFlags.None && description.StructureByteStride == 0;
    }

    private static bool PrepareUpload(DeviceContext context, DataBox source, Resource resource, int subresource, out UploadAttempt attempt, bool retry = false)
    {
        attempt = default;
        if (!GraphicsActive()) return false;
        try
        {
            if (_native == null || _context == null || !_context.TryGetTarget(out DeviceContext? expected) || !ReferenceEquals(context, expected) ||
                context.NativePointer == IntPtr.Zero || context.TypeInfo != DeviceContextType.Immediate ||
                resource is not Buffer buffer || !Buffers.TryGetValue(buffer, out UploadState? state)) return false;
            if (!state.Owner.Gate.IsHeldByCurrentThread || state.Owner.DrawDepth <= 0 || state.Owner.DrawThread != _thread) return false;
            if (state.Owner.Retired ||
                !state.Owner.Manager.TryGetTarget(out _) || !state.Array.TryGetTarget(out GraphicsArray<AtlasQuad>? array) || _native.Disposed(array)) return false;
            if (!ReferenceEquals(_native.Buffer(array), buffer) || buffer.NativePointer == IntPtr.Zero || !ReferenceEquals(_native.Context(array), context) ||
                !MatchesSource(source, _native.Source(array), _native.Size(array), buffer.Description, subresource))
            {
                // An unrecognized native call still owns its original arguments. Do not schedule a CPU-shadow
                // overwrite of another source; only a known outstanding transaction needs delayed dirty recovery.
                state.Dirty.Fail();
                return false;
            }
            if (state.Dirty.Capacity != _native.Size(array) || state.Source != source.DataPointer)
            { state.Dirty.Resize(_native.Size(array)); state.Source = source.DataPointer; state.Identity++; }
            if (!retry && !_native.Dirty(array)) { state.Dirty.Fail(); return false; }
            attempt = state.Begin();
            return true;
        }
        catch (Exception ex) { Stop(ex.Message); return false; }
    }

    internal static UploadState CloseInvalidated(ref UploadAttempt attempt)
    {
        UploadAttempt previous = attempt;
        attempt = default; // The wrapper finalizer must never close the old transaction a second time.
        UploadState? state = previous.State;
        bool safe = Ready && previous.SameBinding && state != null && state.Owner.Depth == 0 && !state.Owner.Retired;
        previous.Finish(false);
        if (!safe) throw new InvalidOperationException("Atlas partial upload invalidated without a safe full retry");
        return state!;
    }

    private static bool RetryFull(DeviceContext context, DataBox source, Resource resource, int subresource, ref UploadAttempt attempt)
    {
        long submitted = attempt.SubmittedBytes;
        int calls = attempt.SubmittedCalls;
        UploadState previous = CloseInvalidated(ref attempt);
        if (!PrepareUpload(context, source, resource, subresource, out UploadAttempt fresh, retry: true) ||
            !ReferenceEquals(fresh.State, previous) || !fresh.OwnsPlan || !fresh.Plan.Full || previous.Owner.Depth != 0)
        {
            fresh.Finish(false);
            throw new InvalidOperationException("Atlas partial upload full retry could not revalidate the native binding");
        }
        attempt = fresh.RequireCommit().AddSubmitted(submitted, calls);
        return true;
    }

    internal sealed class DataWriteHook
    {
        public static void Prefix(object __instance, out Mutation __state)
        {
            __state = BeginMutation(((AtlasQuadManager.InternalManagedAtlasQuad)__instance)._pm, kind: ScopeKind.Write);
            if (__state.Owner != null) Mark(__instance, __state.Owner);
        }
        public static Exception? Finalizer(Exception? __exception, Mutation __state) => EndMutation(__exception, __state);
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original) => AtlasWriteCoverage.RequireIl(original, instructions);
    }
    internal sealed class DrawHook
    {
        public static void Prefix(AtlasQuadManager __instance, out Mutation __state)
        {
            __state = default;
            // BeginDraw already owns ContextLock. A worker must never acquire Owner then wait for that context.
            if ((Ready || Volatile.Read(ref _synchronizing) != 0) && Environment.CurrentManagedThreadId != _thread)
            {
                Stop("Atlas draw requested outside the graphics thread");
                throw new InvalidOperationException("Atlas draw requires the graphics thread before acquiring its owner gate");
            }
            __state = BeginMutation(__instance, kind: ScopeKind.Draw);
        }
        public static Exception? Finalizer(Exception? __exception, Mutation __state) => EndMutation(__exception, __state);
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original) => AtlasWriteCoverage.RequireIl(original, instructions);
    }
    internal sealed class CreateHook
    {
        public static void Prefix(AtlasQuadManager __instance, int subIndex, out Mutation __state) => __state = BeginMutation(__instance, subIndex);
        public static Exception? Finalizer(Exception? __exception, Mutation __state) => EndMutation(__exception, __state);
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original) => AtlasWriteCoverage.RequireIl(original, instructions);
    }
    internal sealed class QuadRemoveHook
    {
        public static void Prefix(object __instance, out Mutation __state) => __state = BeginMutation(((AtlasQuadManager.InternalManagedAtlasQuad)__instance)._pm);
        public static Exception? Finalizer(Exception? __exception, Mutation __state) => EndMutation(__exception, __state);
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original) => AtlasWriteCoverage.RequireIl(original, instructions);
    }
    internal sealed class ClearHook
    {
        public static void Prefix(AtlasQuadManager __instance, out Mutation __state) => __state = BeginMutation(__instance);
        public static Exception? Finalizer(Exception? __exception, Mutation __state) => EndMutation(__exception, __state);
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original) => AtlasWriteCoverage.RequireIl(original, instructions);
    }
    internal sealed class RetireHook
    {
        public static void Prefix(AtlasQuadManager __instance, out Mutation __state) => __state = BeginMutation(__instance, retire: true);
        public static Exception? Finalizer(Exception? __exception, Mutation __state) => EndMutation(__exception, __state);
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original) => AtlasWriteCoverage.RequireIl(original, instructions);
    }
    internal sealed class UploadHook
    {
        public static bool Prefix(DeviceContext __instance, DataBox __0, Resource __1, int __2, out UploadAttempt __state)
        {
            if (!PrepareUpload(__instance, __0, __1, __2, out __state) || !__state.OwnsPlan || __state.Plan.Full) return true;
            __state = __state.RequireCommit();
            // No catch: native API failures propagate unchanged; Finalizer keeps the transaction pending/full.
            for (int i = 0; i < __state.Plan.RangeCount; i++)
            {
                if (!Ready || !__state.CanContinue) return RetryFull(__instance, __0, __1, __2, ref __state);
                AtlasUploadRange range = __state.Plan.GetRange(i);
                int left = checked(range.Start * Unsafe.SizeOf<AtlasQuad>());
                int right = checked((range.Start + range.Count) * Unsafe.SizeOf<AtlasQuad>());
                var region = new ResourceRegion { Left = left, Right = right, Top = 0, Bottom = 1, Front = 0, Back = 1 };
                __instance.UpdateSubresource(__1, __2, region, IntPtr.Add(__0.DataPointer, left), __0.RowPitch, __0.SlicePitch);
                __state = __state.AddSubmitted(right - left);
                if (!Ready || !__state.CanContinue) return RetryFull(__instance, __0, __1, __2, ref __state);
            }
            if (!Ready || !__state.CanContinue) return RetryFull(__instance, __0, __1, __2, ref __state);
            if (!_logged)
            {
                _logged = true;
                Log.Info(SingleRange
                    ? "Partial atlas uploads active: one coalesced bounding dirty span on native Static buffers"
                    : "Partial atlas uploads active: exact dirty ranges on native Static buffers");
            }
            return false;
        }
        public static Exception? Finalizer(Exception? __exception, UploadAttempt __state)
        {
            bool committed;
            try { committed = __state.Finish(__exception == null && Ready); }
            catch (Exception ex)
            {
                Stop(ex.Message);
                if (__exception == null && __state.RequiresCommit)
                    return new InvalidOperationException("Atlas partial upload could not commit", ex);
                return __exception;
            }
            if (__exception == null && __state.RequiresCommit && !committed)
                return new InvalidOperationException("Atlas partial upload changed before completion");
            if (__exception == null && __state.State != null && Environment.CurrentManagedThreadId == _thread)
            {
                if (__state.NativeFull) _fullUploads++; else _partialUploads++;
                _uploadedBytes += __state.SubmittedBytes + (__state.NativeFull ? __state.CapacityBytes : 0);
                _fullEquivalentBytes += __state.CapacityBytes;
                _uploadCalls += __state.SubmittedCalls + (__state.NativeFull ? 1 : 0);
            }
            return __exception;
        }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            RequireUploadIl(UploadCore, PatchProcessor.GetOriginalInstructions(UploadCore));
            return RequireUploadIl(UploadTarget, instructions);
        }
    }

    private sealed class NativeAccess
    {
        internal readonly Type Type;
        internal readonly Func<object, int> Size;
        internal readonly Func<object, bool> Dirty, Disposed;
        internal readonly Func<object, IntPtr> Source;
        internal readonly Func<object, Buffer?> Buffer;
        internal readonly Func<object, DeviceContext> Context;
        internal NativeAccess(Assembly backend)
        {
            Type = backend.GetType("Halfling.Graphics.D3D11.D3D11StaticGraphicsArray`1", true)!.MakeGenericType(typeof(AtlasQuad));
            Size = Getter<int>(Type, "_size");
            Dirty = Getter<bool>(Type, "_dirty");
            Disposed = Getter<bool>(Type, "_disposed");
            Source = Getter<IntPtr>(Type, "_data");
            Buffer = Getter<Buffer?>(Type, "_vertices");
            var method = new DynamicMethod("AtlasArrayContext", typeof(DeviceContext), [typeof(object)], typeof(AtlasPartialUploads).Module, true);
            ILGenerator il = method.GetILGenerator();
            FieldInfo gfx = AccessTools.Field(Type, "_gfx");
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, Type); il.Emit(OpCodes.Ldfld, gfx);
            il.Emit(OpCodes.Callvirt, AccessTools.PropertyGetter(gfx.FieldType, "Context")); il.Emit(OpCodes.Ret);
            Context = method.CreateDelegate<Func<object, DeviceContext>>();
        }
        private static Func<object, T> Getter<T>(Type owner, string name)
        {
            FieldInfo field = AccessTools.Field(owner, name) ?? throw new MissingFieldException(owner.FullName, name);
            var method = new DynamicMethod("AtlasRead" + name, typeof(T), [typeof(object)], typeof(AtlasPartialUploads).Module, true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, owner); il.Emit(OpCodes.Ldfld, field);
            if (field.FieldType.IsPointer) il.Emit(OpCodes.Conv_I);
            else if (field.FieldType != typeof(T)) throw new InvalidOperationException("Unexpected atlas field type " + name);
            il.Emit(OpCodes.Ret);
            return method.CreateDelegate<Func<object, T>>();
        }
    }
}
