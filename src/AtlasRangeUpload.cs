using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Rendering;
using Halfling.Collections;
using Halfling.Graphics;
using Halfling.Graphics.D3D11;
using HarmonyLib;
using SharpDX.Direct3D11;
using D3DBuffer = SharpDX.Direct3D11.Buffer;

namespace UnofficialPatch;

/// <summary>
/// Ship atlas geometry upload proportional to what changed. Native Dynamic arrays upload their whole capacity
/// (Map WRITE_DISCARD + copy of <c>_size</c> elements) whenever any quad changed, and every quad removal marks the
/// whole array dirty through a zero-length <c>CopyTo</c>. Ship atlas lists (the only Dynamic AtlasQuadManager lists)
/// are created with <see cref="AtlasRangeArray"/> instead: a DEFAULT buffer that keeps unchanged bytes, updated with
/// UpdateSubresource for at most <see cref="AtlasDirtySpans.MaxSpans"/> coalesced dirty spans. Removing the last quad
/// of a list only shrinks the list count (its bytes are not drawn and nothing is copied). Render-only; no sim effect.
/// Off: <c>UNOFFICIAL_PATCH_ATLAS_RANGE_UPLOAD=0</c>. A changed WDX/HalflingCore contract rolls back every patch; a foreign
/// Harmony owner on a dependency keeps new lists and removals native (checked every frame with the other features).
/// </summary>
internal static class AtlasRangeUpload
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("UNOFFICIAL_PATCH_ATLAS_RANGE_UPLOAD"));
    internal static bool IsRequested(string? value) => value != "0";

    private static readonly Type QuadType = typeof(AtlasQuadManager).GetNestedType("InternalManagedAtlasQuad", BindingFlags.NonPublic)
        ?? throw new MissingMemberException(nameof(AtlasQuadManager), "InternalManagedAtlasQuad");

    /// <summary>Patch order matters: MoveIndex before Dispose, so Dispose's rebuilt body calls (not inlines) the patched MoveIndex.</summary>
    internal static readonly (MethodBase Target, string IlSha256)[] Targets =
    [
        (AccessTools.Constructor(typeof(AtlasQuadManager), [typeof(Ship), typeof(PerformanceMode)]), "FFCF59C27CF15BC7BB9A9FBD12683F78170F2BBDE9410AB1F3B90DAEF4654F89"),
        (AccessTools.Method(typeof(AtlasQuadManager), nameof(AtlasQuadManager.CreateQuad)), "9D93290207D830F90B9C4077B96BB6DB5480023A0E8AE98FAD8E42F4D9C21D6B"),
        (AccessTools.Method(QuadType, "MoveIndex"), "1C40016E8B977ED3CA8E742EAC1293A66F4B33C2A35CF5D5F3673AA606F255F0"),
        (AccessTools.Method(QuadType, "Dispose"), "991E383472BDE744E1BCB42D83D464ED4DF0E28C5B772566A45DE09DA3D3BF18"),
    ];

    /// <summary>
    /// Foreign owners here keep new lists and removals native: the patched targets, the quad writer and the native list
    /// removals our <see cref="RemoveAt"/> bypasses. Existing range arrays stay correct (every write uses their virtuals).
    /// </summary>
    internal static readonly MethodBase[] Dependencies =
    [
        .. Array.ConvertAll(Targets, t => t.Target),
        AccessTools.PropertySetter(QuadType, "Data"),
        AccessTools.Method(typeof(GraphicsList<AtlasQuad>), nameof(GraphicsList<AtlasQuad>.RemoveAt)),
        AccessTools.Method(typeof(GraphicsList<AtlasQuad>), nameof(GraphicsList<AtlasQuad>.RemoveRange)),
    ];

    /// <summary>Exact native WDX/HalflingCore bodies whose contracts the range array and list swap rely on.</summary>
    internal const string NativeIl = "1FDCF293E20A7A233A9284C97EABE0A41059119C6D6A29BCE29B8A98114B5075";

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? Array.ConvertAll(Targets, t => (t.Target, typeof(AtlasRangeUpload))) : [];

    private static readonly ConstructorInfo ListCtor = AccessTools.Constructor(typeof(GraphicsList<AtlasQuad>), [typeof(GraphicsManager), typeof(PerformanceMode)]);
    private static readonly MethodInfo NewListMethod = AccessTools.Method(typeof(AtlasRangeUpload), nameof(NewList));
    private static readonly MethodInfo RemoveAtMethod = AccessTools.Method(typeof(AtlasRangeUpload), nameof(RemoveAt));

    private static int _ready, _created;
    private static string? _foreign, _loggedForeign;
    private static HashSet<(Module, int)>? _dependencySet;
    internal static bool Ready => Volatile.Read(ref _ready) != 0;
    /// <summary>New lists and removals use the range path: ready and no foreign owner on a dependency.</summary>
    internal static bool Active => Ready && Volatile.Read(ref _foreign) == null;

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        string pin = Array.Find(Targets, t => t.Target.Module == original.Module && t.Target.MetadataToken == original.MetadataToken).IlSha256
            ?? throw new InvalidOperationException("Unknown atlas range target " + original);
        List<CodeInstruction> code = Patcher.RequireIl(instructions, pin, original.DeclaringType!.Name + "." + original.Name);
        if (!Patcher.TryDisableInlining(original, out string? error)) throw new NotSupportedException(error);
        int replaced = 0;
        foreach (CodeInstruction ci in code)
        {
            if (ci.opcode == OpCodes.Newobj && Equals(ci.operand, ListCtor))
            {
                ci.opcode = OpCodes.Call;
                ci.operand = NewListMethod;
                replaced++;
            }
            else if (ci.opcode == OpCodes.Callvirt && ci.operand is MethodInfo { Name: "RemoveAt" } m &&
                    (m.DeclaringType == typeof(GraphicsList<AtlasQuad>) || m.DeclaringType == typeof(ListBase<AtlasQuad>)))
            {
                ci.opcode = OpCodes.Call;
                ci.operand = RemoveAtMethod;
                replaced++;
            }
        }
        if (replaced != 1) throw new InvalidOperationException($"{original.Name}: expected one atlas list call, found {replaced}");
        return code;
    }

    /// <summary>Throws when the game changed (rolls back every patch).</summary>
    internal static bool Initialize()
    {
        if (!Requested) return true;
        ValidateNative();
        foreach ((MethodBase target, _) in Targets)
        {
            Patches? patches = Harmony.GetPatchInfo(target);
            if (patches == null || !patches.Transpilers.Exists(p => p.owner == Patcher.HarmonyId && p.PatchMethod.DeclaringType == typeof(AtlasRangeUpload)))
                throw new InvalidOperationException("Atlas range upload hook missing on " + target.DeclaringType?.Name + "." + target.Name);
        }
        _dependencySet = Dependencies.Select(m => (m.Module, m.MetadataToken)).ToHashSet();
        SetForeign(ScanForeign());
        Volatile.Write(ref _ready, 1);
        Log.Info("Atlas range uploads ready");
        return true;
    }

    /// <summary>Bodies of every native member the swap depends on, plus field and abstract-member layout.</summary>
    internal static List<MethodBase> NativeDependencies()
    {
        const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        static IEnumerable<MethodBase> Bodies(Type type) =>
            type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)).Where(m => m.GetMethodBody() != null).OrderBy(m => m.MetadataToken);
        static MethodInfo Generic(string name, int arrays) => typeof(D3D11GraphicsManager).GetMethods(all)
            .Single(m => m.Name == name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == arrays && m.GetParameters().Length == arrays + 2)
            .MakeGenericMethod(Enumerable.Repeat(typeof(AtlasQuad), arrays).ToArray());
        return
        [
            .. Bodies(typeof(GraphicsArray<AtlasQuad>)),
            .. Bodies(typeof(D3D11BaseGraphicsArray<AtlasQuad>)),
            .. Bodies(typeof(D3D11DynamicGraphicsArray<AtlasQuad>)),
            .. Bodies(typeof(GraphicsList<AtlasQuad>)),
            Generic("Draw", 1),
            Generic("DrawInterleaved", 2),
            typeof(D3D11GraphicsManager).GetMethods(all).Single(m => m.Name == "CreateGraphicsArray" && m.GetParameters()[0].ParameterType == typeof(int)).MakeGenericMethod(typeof(AtlasQuad)),
            AccessTools.Method(typeof(D3D11GraphicsManager), "AddOwnedDisposable"),
            AccessTools.Method(typeof(D3D11GraphicsManager), "RemoveOwnedDisposable"),
            AccessTools.PropertyGetter(typeof(D3D11GraphicsManager), "ContextLock"),
        ];
    }

    internal static void ValidateNative()
    {
        Patcher.RequireIl(NativeDependencies().SelectMany(m => PatchProcessor.GetOriginalInstructions(m)), NativeIl, "Atlas range upload native dependencies");
        static string Abstracts(Type type) => string.Join(",", type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.IsAbstract).Select(m => m.Name).Order(StringComparer.Ordinal));
        if (Abstracts(typeof(GraphicsArray<AtlasQuad>)) != "GetRef,GetUnsafeTempPointer,Resize,get_DebugName,get_Item,get_Size,set_DebugName,set_Item" ||
            Abstracts(typeof(D3D11BaseGraphicsArray<AtlasQuad>)) != "UpdateDirtyData")
            throw new NotSupportedException("Graphics array abstract members changed: " + Abstracts(typeof(GraphicsArray<AtlasQuad>)) + " / " + Abstracts(typeof(D3D11BaseGraphicsArray<AtlasQuad>)));
        string fields = string.Join(",", typeof(GraphicsList<AtlasQuad>).GetFields(BindingFlags.DeclaredOnly | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            .Select(f => f.FieldType.Name + " " + f.Name));
        if (fields != "GraphicsArray`1 _array,Int32 _count,Boolean _disposed")
            throw new NotSupportedException("GraphicsList layout changed: " + fields);
    }

    internal static bool IsDependency(MethodBase method) => _dependencySet != null && _dependencySet.Contains((method.Module, method.MetadataToken));

    /// <summary>Per-frame result of the shared foreign-owner scan (<see cref="BlueprintToggleEvaluation"/>); null = none.</summary>
    internal static void SetForeign(string? reason)
    {
        Volatile.Write(ref _foreign, reason);
        if (reason != null && Interlocked.Exchange(ref _loggedForeign, reason) == null)
            Log.Info("Atlas range uploads paused (native lists and removals): " + reason);
    }

    private static string? ScanForeign()
    {
        foreach (MethodBase method in Dependencies)
        {
            Patches? patches = Harmony.GetPatchInfo(method);
            if (patches == null) continue;
            foreach (string owner in patches.Owners)
                if (owner != Patcher.HarmonyId) return "foreign Harmony owner " + owner + " on " + method.DeclaringType?.Name + "." + method.Name;
        }
        return null;
    }

    internal static void Disable() => Volatile.Write(ref _ready, 0);

    /// <summary>Replaces <c>new GraphicsList&lt;AtlasQuad&gt;(gfx, perfMode)</c> in the atlas manager.</summary>
    internal static GraphicsList<AtlasQuad> NewList(GraphicsManager gfx, PerformanceMode perfMode)
    {
        if (perfMode != PerformanceMode.Dynamic || gfx is not D3D11GraphicsManager device || !Active) return new GraphicsList<AtlasQuad>(gfx, perfMode);
        GraphicsList<AtlasQuad> list = CreateRangeList(device);
        if (Interlocked.Exchange(ref _created, 1) == 0) Log.Info("Atlas range uploads active");
        return list;
    }

    internal static GraphicsList<AtlasQuad> CreateRangeList(D3D11GraphicsManager device)
    {
        // Zero capacity: the native placeholder owns no buffer or memory; swap in ours before anything can use it.
        var list = new GraphicsList<AtlasQuad>(device, 0, PerformanceMode.Dynamic);
        GraphicsArray<AtlasQuad> placeholder = list._array;
        Unsafe.AsRef(in list._array) = new AtlasRangeArray(device, 32);
        placeholder.Dispose();
        return list;
    }

    /// <summary>Replaces <c>list.RemoveAt(index)</c>; the atlas manager only ever removes its last quad.</summary>
    internal static void RemoveAt(GraphicsList<AtlasQuad> list, int index)
    {
        // Native: zero-length CopyTo (marks the whole array dirty), then _count--. Bytes past Count are never drawn.
        if (index >= 0 && index == list._count - 1 && list._array is AtlasRangeArray && Volatile.Read(ref _foreign) == null) list._count = index;
        else list.RemoveAt(index);
    }
}

/// <summary>
/// Sorted, disjoint, non-adjacent half-open element spans, at most <see cref="MaxSpans"/>; an extra span merges the two
/// spans with the smallest gap, so uploads never exceed MaxSpans calls per array. Not thread-safe (caller locks).
/// </summary>
internal sealed class AtlasDirtySpans
{
    internal const int MaxSpans = 4;
    private readonly int[] _lo = new int[MaxSpans + 1], _hi = new int[MaxSpans + 1];
    internal int Count; // written only here, under the owner's lock
    internal int Lo(int i) => _lo[i];
    internal int Hi(int i) => _hi[i];
    internal void Clear() => Count = 0;

    internal void Add(int lo, int hi)
    {
        if (lo >= hi) return;
        int n = Count, i = 0;
        while (i < n && _hi[i] < lo) i++;
        int j = i;
        while (j < n && _lo[j] <= hi)
        {
            if (_lo[j] < lo) lo = _lo[j];
            if (_hi[j] > hi) hi = _hi[j];
            j++;
        }
        int shift = 1 - (j - i); // spans [i, j) become one span at i
        if (shift > 0) for (int k = n - 1; k >= j; k--) { _lo[k + 1] = _lo[k]; _hi[k + 1] = _hi[k]; }
        else if (shift < 0) for (int k = j; k < n; k++) { _lo[k + shift] = _lo[k]; _hi[k + shift] = _hi[k]; }
        _lo[i] = lo;
        _hi[i] = hi;
        n += shift;
        if (n > MaxSpans)
        {
            int best = 0;
            for (int k = 1; k < n - 1; k++)
                if (_lo[k + 1] - _hi[k] < _lo[best + 1] - _hi[best]) best = k;
            _hi[best] = _hi[best + 1];
            for (int k = best + 1; k < n - 1; k++) { _lo[k] = _lo[k + 1]; _hi[k] = _hi[k + 1]; }
            n--;
        }
        Count = n;
    }

    internal void AddAll(AtlasDirtySpans other)
    {
        for (int k = 0; k < other.Count; k++) Add(other._lo[k], other._hi[k]);
    }
}

/// <summary>
/// D3D11 atlas array on a DEFAULT buffer. Same CPU shadow, bounds/dispose/ownership semantics as the native Dynamic
/// array; dirty state is spans instead of one flag. Every native mutation path is a virtual of this class: indexer set,
/// GetRef(!readOnly), GetUnsafeTempPointer(false) (CopyTo/CopyFrom; marks every written element) and Resize.
/// Threading (game source): frames run on one thread (D3D11Window._DoFrame: update, then draw); quads are written by the
/// main thread or FastParallel workers joined inside the update, or by a load task on ships not yet drawn, and uploads run
/// only from Draw. The model is not trusted: one lock covers every element access with its mark, the whole upload
/// (spans cleared only after success) and resize/dispose, so the shadow and buffer cannot change or be freed mid-copy.
/// GetRef/pointer marks precede the caller's write, so they also stay pending for one more upload. Escaped refs and
/// pointers otherwise follow the native lifetime/phase rules (valid until the next resize or draw); writes through them
/// are not synchronized. Lock order everywhere: ContextLock before the array lock; array-lock holders never take ContextLock.
/// </summary>
internal sealed unsafe class AtlasRangeArray : D3D11BaseGraphicsArray<AtlasQuad>
{
    private readonly D3D11GraphicsManager _gfx;
    private readonly AtlasDirtySpans _spans = new(), _escaped = new();
    private AtlasQuad* _data;
    private int _size, _extent, _sync; // _extent: elements ever marked (bytes the GPU copy must hold), <= _size
    private bool _disposed;
    private WeakReference<IDisposable>? _wr;

    internal AtlasRangeArray(D3D11GraphicsManager gfx, int size) : base(PerformanceMode.Dynamic)
    {
        _gfx = gfx;
        _gfx.AddOwnedDisposable(this, out _wr);
        Enter();
        try { ResizeImpl(size, keepData: false); }
        finally { Exit(); }
    }

#pragma warning disable MA0055 // native arrays free their CPU shadow and ownership entry the same way
    ~AtlasRangeArray() => Dispose(false);
#pragma warning restore MA0055

    public override AtlasQuad this[int i]
    {
        get
        {
            Enter();
            try
            {
                Check(i);
                return _data[i];
            }
            finally { Exit(); }
        }
        set
        {
            Enter();
            try
            {
                Check(i);
                _data[i] = value;
                Mark(i, i + 1);
            }
            finally { Exit(); }
        }
    }

    public override int Size => _size;

    public override ref AtlasQuad GetRef(int index, bool readOnly = false)
    {
        Enter();
        try
        {
            Check(index);
            if (!readOnly) MarkEscaped(index, index + 1);
            return ref _data[index];
        }
        finally { Exit(); }
    }

    public override AtlasQuad* GetUnsafeTempPointer(bool readOnly)
    {
        Enter();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!readOnly) MarkEscaped(0, _size);
            return _data;
        }
        finally { Exit(); }
    }

    public override void Resize(int size, bool keepData = true)
    {
        Enter();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(size);
            ResizeImpl(size, keepData);
        }
        finally { Exit(); }
    }

    public override void UploadHint()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UpdateDirtyData();
    }

    public override void UpdateDirtyData()
    {
        if (Volatile.Read(ref _spans.Count) == 0) return;
        // Lock order: ContextLock, then the array lock (native BeginDraw/Draw hold ContextLock first; no array-lock
        // holder ever waits for ContextLock). ContextLock is reentrant for the render thread.
        using (_gfx.ContextLock.EnterScope())
        {
            Enter();
            try
            {
                if (_disposed || _spans.Count == 0) return;
                if (_size > 0 && _vertices != null)
                {
                    for (int k = 0; k < _spans.Count; k++)
                    {
                        int lo = _spans.Lo(k), hi = Math.Min(_spans.Hi(k), _size);
                        if (lo >= hi) continue;
                        var box = new ResourceRegion(lo * sizeof(AtlasQuad), 0, 0, hi * sizeof(AtlasQuad), 1, 1);
                        _gfx.Context.UpdateSubresource(_vertices, 0, box, (IntPtr)(_data + lo), 0, 0);
                    }
                }
                // Only after every copy succeeded; escaped marks get one more upload (their write may follow this one).
                _spans.Clear();
                _spans.AddAll(_escaped);
                _escaped.Clear();
            }
            finally { Exit(); }
        }
    }

#pragma warning disable CA1816 // native Dispose(bool) pattern: GraphicsArray.Dispose() is non-virtual
    public override void Dispose(bool disposing) // protected at run time (publicized reference)
    {
        Enter();
        try
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    GC.SuppressFinalize(this);
                    _vertices?.Dispose();
                }
                if (_data != null)
                {
                    NativeMemory.AlignedFree(_data);
                    _data = null;
                }
                _gfx.RemoveOwnedDisposable(ref _wr);
                _disposed = true;
            }
        }
        finally { Exit(); }
        base.Dispose(disposing);
    }
#pragma warning restore CA1816

    private void Check(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 0 || index >= _size) throw new ArgumentOutOfRangeException(nameof(index));
    }

    // Callers hold the lock.
    private void Mark(int lo, int hi)
    {
        _spans.Add(lo, hi);
        if (hi > _extent) _extent = hi;
    }

    private void MarkEscaped(int lo, int hi)
    {
        Mark(lo, hi);
        _escaped.Add(lo, hi);
    }

    private void Enter()
    {
        if (Interlocked.CompareExchange(ref _sync, 1, 0) == 0) return;
        var spin = new SpinWait();
        do spin.SpinOnce(); while (Interlocked.CompareExchange(ref _sync, 1, 0) != 0);
    }

    private void Exit() => Volatile.Write(ref _sync, 0);

    /// <summary>Native ResizeImpl with a DEFAULT buffer; kept data (every element ever marked) uploads at the next draw. Lock held.</summary>
    private void ResizeImpl(int size, bool keepData)
    {
        _vertices?.Dispose();
        _vertices = null;
        if (size > 0)
        {
            nuint bytes = (nuint)size * (nuint)sizeof(AtlasQuad);
            if (keepData) _data = (AtlasQuad*)NativeMemory.AlignedRealloc(_data, bytes, 16u);
            else
            {
                NativeMemory.AlignedFree(_data);
                _data = (AtlasQuad*)NativeMemory.AlignedAlloc(bytes, 16u);
            }
            var description = new BufferDescription
            {
                BindFlags = BindFlags.VertexBuffer,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None,
                SizeInBytes = checked((int)bytes),
                StructureByteStride = 0,
                Usage = ResourceUsage.Default,
            };
            _vertices = new D3DBuffer(_gfx.Device, description);
        }
        else
        {
            NativeMemory.AlignedFree(_data);
            _data = null;
        }
        _size = size;
        _extent = keepData ? Math.Min(_extent, size) : 0;
        _spans.Clear();
        _escaped.Clear();
        _spans.Add(0, _extent);
    }
}
