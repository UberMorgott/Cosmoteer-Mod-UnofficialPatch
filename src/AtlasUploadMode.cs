using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Rendering;
using Halfling;
using Halfling.Graphics;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Opt-in experiment selecting the existing WDX Static atlas backend. Native list construction, CPU shadow updates,
/// resizing and rendering remain intact. Static uses DEFAULT buffers/UpdateSubresource; Dynamic uses WriteDiscard.
/// </summary>
internal static class AtlasUploadMode
{
    private const string BackendSha256 = "9BA521D4FB95456EB522036ACD83D3E1EC9BC80868AB2B48ABD554BC63B1221D";
    private static readonly Guid BackendMvid = new("7121448b-beeb-4b38-aaf7-77caa880be66");
    private static readonly string? RequestedMode = Environment.GetEnvironmentVariable("PERFORMANCE_ATLAS_STATIC");
    internal static readonly bool Enabled = IsRequested(RequestedMode);
    private static int _converted;

    internal static readonly ConstructorInfo Target = AccessTools.Constructor(typeof(AtlasQuadManager), [typeof(Ship), typeof(PerformanceMode)])
        ?? throw new MissingMethodException(nameof(AtlasQuadManager), ".ctor(Ship,PerformanceMode)");

    internal static bool IsRequested(string? value) => value is "1" or "2" or "3";

    internal static PerformanceMode Select(bool enabled, PerformanceMode mode) =>
        enabled && mode == PerformanceMode.Dynamic ? PerformanceMode.Static : mode;

    internal static PerformanceMode SelectRequest(string? requested, bool ready, PerformanceMode mode) =>
        Select(IsRequested(requested) && (requested == "1" || ready), mode);

    public static void Prefix(ref PerformanceMode perfMode)
    {
        PerformanceMode selected = SelectRequest(RequestedMode, AtlasPartialUploads.Ready, perfMode);
        if (selected == perfMode) return;
        perfMode = selected;
        if (Interlocked.Increment(ref _converted) == 1)
            Log.Info("Native atlas Static backend experiment active: Dynamic -> Static (PERFORMANCE_ATLAS_STATIC=" + RequestedMode + ")");
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = Patcher.RequireIl(instructions, "FFCF59C27CF15BC7BB9A9FBD12683F78170F2BBDE9410AB1F3B90DAEF4654F89", "AtlasQuadManager.ctor");
        if (Enabled)
        {
            Assembly backend = App.Graphics.GetType().Assembly;
            if (backend.GetName().Name != "HalflingPlatformWDX" || backend.ManifestModule.ModuleVersionId != BackendMvid)
                throw new InvalidOperationException("Atlas Static experiment requires the pinned WDX graphics backend");
            // Pins factory selection plus both native backends' construction/update/resize bodies, not generic detours.
            RequireBackendBinary(backend.Location);
        }
        return code;
    }

    internal static void RequireBackendBinary(string path)
    {
        using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (actual != BackendSha256)
            throw new InvalidOperationException("Atlas Static WDX dependency binary changed (sha256 " + actual + ")");
    }
}
