using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships.Rendering;
using Halfling.Graphics;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

public sealed class AtlasDrawCloneTests
{
    private static MethodInfo Source(string name = nameof(FakeHost.Draw)) => typeof(FakeHost).GetMethod(name)!.MakeGenericMethod(typeof(AtlasQuad));
    private static MethodInfo UploadMethod => typeof(FakeArray).GetMethod(nameof(FakeArray.Upload))!;
    private static GraphicsList<AtlasQuad> List() => (GraphicsList<AtlasQuad>)RuntimeHelpers.GetUninitializedObject(typeof(GraphicsList<AtlasQuad>));

    [Fact]
    public void CallbackReplacesOnlyUploadAndReceivesActualReceiverListAndCurrentArguments()
    {
        var trace = new List<string>(); var receiver = new FakeArray(trace); var input = new FakeArray([]);
        var host = new FakeHost(trace, receiver); var list = List();
        host.Draw(input, 5, 4);
        Assert.Equal(new[] { "before", "native", "after:7:3" }, trace); trace.Clear();
        var observed = new List<(GraphicsArray<AtlasQuad> Array, GraphicsList<AtlasQuad> Owner, int Start, int Count)>();
        Action<GraphicsArray<AtlasQuad>, GraphicsList<AtlasQuad>, int, int> callback = (array, owner, start, count) =>
        {
            observed.Add((array, owner, start, count));
            trace.Add("callback"); receiver.Upload();
        };
        var clone = AtlasDrawClone.Create(Source(), UploadMethod, callback);
        Assert.Same(callback, clone.Target); // Callback is bound once, not dynamically invoked through object[].
        clone(host, input, 5, 4, list);
        var actual = Assert.Single(observed);
        Assert.Same(receiver, actual.Array); Assert.Same(list, actual.Owner); Assert.Equal(7, actual.Start); Assert.Equal(3, actual.Count);
        Assert.Equal(new[] { "before", "callback", "native", "after:7:3" }, trace);
        trace.Clear(); clone(host, input, 5, 0, list); Assert.Empty(trace);
    }
    [Theory]
    [InlineData("before")]
    [InlineData("callback")]
    [InlineData("after")]
    public void SourceAndCallbackExceptionsKeepIdentityAndOrder(string phase)
    {
        var trace = new List<string>(); var array = new FakeArray(trace);
        var sentinel = new InvalidOperationException("clone " + phase); var host = new FakeHost(trace, array);
        if (phase == "before") host.BeforeFailure = sentinel;
        if (phase == "after") host.AfterFailure = sentinel;
        var clone = AtlasDrawClone.Create(Source(), UploadMethod, (_, _, _, _) =>
        {
            trace.Add("callback"); if (phase == "callback") throw sentinel; array.Upload();
        });
        Assert.Same(sentinel, Assert.Throws<InvalidOperationException>(() => clone(host, array, 5, 4, List())));
        Assert.Equal(phase == "before" ? new[] { "before" } : phase == "callback" ? new[] { "before", "callback" } : new[] { "before", "callback", "native", "after:7:3" }, trace);
    }
    [Fact]
    public void ActualBackendCastAndOriginalCallvirtNullCheckPrecedeCallback()
    {
        var trace = new List<string>(); var input = new FakeArray(trace); var host = new FakeHost(trace, null);
        int calls = 0; var clone = AtlasDrawClone.Create(Source(), UploadMethod, (_, _, _, _) => calls++);
        Assert.Throws<InvalidCastException>(() => clone(new object(), input, 1, 1, List()));
        Assert.Throws<NullReferenceException>(() => clone(null!, input, 1, 0, List()));
        Assert.Empty(trace);
        Assert.Throws<NullReferenceException>(() => clone(host, input, 1, 1, List()));
        Assert.Equal(new[] { "before" }, trace); Assert.Equal(0, calls);
    }
    [Theory]
    [InlineData("open")]
    [InlineData("static")]
    [InlineData("struct")]
    [InlineData("eh")]
    [InlineData("multiple")]
    [InlineData("missing")]
    [InlineData("upload-static")]
    public void UnsupportedBodiesAndSignaturesAreRejected(string shape)
    {
        MethodInfo source = shape switch
        {
            "open" => typeof(FakeHost).GetMethod(nameof(FakeHost.Draw))!,
            "static" => Source(nameof(FakeHost.StaticDraw)),
            "struct" => typeof(StructHost).GetMethod(nameof(StructHost.Draw))!.MakeGenericMethod(typeof(AtlasQuad)),
            "eh" => Source(nameof(FakeHost.WithEh)),
            "multiple" => Source(nameof(FakeHost.Twice)),
            _ => Source(),
        };
        MethodInfo upload = shape switch { "missing" => typeof(FakeArray).GetMethod(nameof(FakeArray.OtherUpload))!, "upload-static" => typeof(FakeArray).GetMethod(nameof(FakeArray.StaticUpload))!, _ => UploadMethod };
        Assert.Throws<NotSupportedException>(() => AtlasDrawClone.Create(source, upload, (_, _, _, _) => { }));
    }
    [Fact]
    public void InstalledClosedNativeBodyIsPreparedWithoutExecutionOrAnyPatch()
    {
        int before = Harmony.GetAllPatchedMethods().Count();
        Assembly backend = Assembly.LoadFrom(System.IO.Path.Combine(GameAssemblies.GameBin, "HalflingPlatformWDX.dll"));
        Type type = backend.GetType("Halfling.Graphics.D3D11.D3D11GraphicsManager", true)!;
        MethodInfo source = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(m => m.Name == "Draw" && m.IsGenericMethodDefinition && m.GetParameters().Length == 3).MakeGenericMethod(typeof(AtlasQuad));
        Assert.Empty(source.GetMethodBody()!.ExceptionHandlingClauses);
        MethodInfo upload = Assert.Single(PatchProcessor.GetOriginalInstructions(source), c => c.operand is MethodInfo m && m.Name == "UpdateDirtyData").operand as MethodInfo ?? throw new InvalidOperationException();
        Assert.False(upload.ContainsGenericParameters);
        var clone = AtlasDrawClone.Create(source, upload, (_, _, _, _) => throw new InvalidOperationException("Native draw must never execute in this structural test"));
        RuntimeHelpers.PrepareDelegate(clone);
        Assert.Equal(before, Harmony.GetAllPatchedMethods().Count()); Assert.Null(Harmony.GetPatchInfo(source));
    }
    // These fixtures deliberately retain the native fixed Draw signature while isolating order and rejection behavior.
    // ReSharper disable UnusedParameter.Local
    private sealed class FakeHost(List<string> trace, FakeArray? receiver)
    {
        internal Exception? BeforeFailure, AfterFailure;
        public void Draw<T>(GraphicsArray<T> array, int start, int count) where T : unmanaged
        {
            if (count == 0) return;
            trace.Add("before"); if (BeforeFailure != null) throw BeforeFailure;
            start += 2; count--;
            receiver!.Upload();
            trace.Add($"after:{start}:{count}"); if (AfterFailure != null) throw AfterFailure;
        }
        public static void StaticDraw<T>(GraphicsArray<T> array, int start, int count) where T : unmanaged { }
        public void WithEh<T>(GraphicsArray<T> array, int start, int count) where T : unmanaged
        { try { receiver!.Upload(); } finally { trace.Add("finally"); } }
        public void Twice<T>(GraphicsArray<T> array, int start, int count) where T : unmanaged { receiver!.Upload(); receiver.Upload(); }
    }
    private struct StructHost
    {
        public void Draw<T>(GraphicsArray<T> array, int start, int count) where T : unmanaged { }
    }
    // ReSharper restore UnusedParameter.Local
    private sealed class FakeArray(List<string> trace) : GraphicsArray<AtlasQuad>(PerformanceMode.Static)
    {
        // Managed-only fixture. The inherited Dispose(bool) is empty; no GPU/native resource is acquired.
        private AtlasQuad _value;
        public override AtlasQuad this[int i] { get => _value; set => _value = value; }
        public override int Size => 1;
        public override string? DebugName { get; set; }
        public override ref AtlasQuad GetRef(int index, bool readOnly = false) => ref _value;
        public override void Resize(int size, bool keepData = true) => throw new NotSupportedException();
        public override unsafe AtlasQuad* GetUnsafeTempPointer(bool readOnly) => throw new NotSupportedException();
        public void Upload() => trace.Add("native");
        public void OtherUpload() => trace.Add("other");
        public static void StaticUpload() { }
    }
}
