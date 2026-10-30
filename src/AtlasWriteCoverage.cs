using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Cosmoteer.Ships.Rendering;
using Halfling.Graphics;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Startup-only coverage preflight for a future atlas write tracker. No writer hooks are installed here. Existing
/// inlined JIT callers cannot be repaired by disabling future inlining, so the caller must also enforce startup.
/// </summary>
internal static class AtlasWriteCoverage
{
    internal static readonly Type InternalQuadType = typeof(AtlasQuadManager).GetNestedType("InternalManagedAtlasQuad", BindingFlags.NonPublic)
        ?? throw new MissingMemberException(nameof(AtlasQuadManager), "InternalManagedAtlasQuad");
    internal static readonly MethodInfo DataSetter = AccessTools.PropertySetter(InternalQuadType, "Data");
    internal static readonly (MethodInfo Target, string IlSha256)[] Targets =
    [
        (DataSetter, "0FAD79703B0A80D946CDB21E6F3E7E61DFFC442609C8245E8083D16642BF23A1"),
        (AccessTools.Method(typeof(AtlasQuadManager), nameof(AtlasQuadManager.CreateQuad)), "9D93290207D830F90B9C4077B96BB6DB5480023A0E8AE98FAD8E42F4D9C21D6B"),
        (AccessTools.Method(InternalQuadType, "Dispose"), "991E383472BDE744E1BCB42D83D464ED4DF0E28C5B772566A45DE09DA3D3BF18"),
        (AccessTools.Method(InternalQuadType, "MoveIndex"), "1C40016E8B977ED3CA8E742EAC1293A66F4B33C2A35CF5D5F3673AA606F255F0"),
        (AccessTools.Method(typeof(AtlasQuadManager), nameof(AtlasQuadManager.Clear)), "95848CE9CF62C18553FE8F3265202F10C5FF269376454F08598B562519F8A452"),
        (AccessTools.Method(typeof(AtlasQuadManager), "Dispose", [typeof(bool)]), "F3FBE1C5DEDD75ABE048078965153EB6F3493EDFBF274A42AE3AD1ADA4C4B575"),
    ];
    internal static readonly (MethodInfo Target, string IlSha256)[] DrawTargets =
    [
        (AccessTools.Method(typeof(AtlasQuadManager), "DrawForEachTexture", [typeof(Texture[])]), "7530813DD97FB83765F2AA25EDF841BCD0D37338EB731661E50C272E9AAFEC5B"),
        (AccessTools.Method(typeof(AtlasQuadManager), "DrawForEachTexture", [typeof((Texture, Texture, Texture)[])]), "1B879892F61FC02C0C4651086378975B2B124FE656517074C73CD31D619F4F9C"),
    ];
    private static readonly (MethodInfo Target, string IlSha256)[] AllTargets = [.. Targets, .. DrawTargets];

    internal static List<CodeInstruction> RequireIl(MethodBase original, IEnumerable<CodeInstruction> instructions)
    {
        foreach ((MethodInfo target, string pin) in AllTargets)
            if (original.Module == target.Module && original.MetadataToken == target.MetadataToken)
                return Patcher.RequireIl(instructions, pin, target.DeclaringType!.FullName + "." + target.Name);
        throw new InvalidOperationException("Unknown atlas writer " + original);
    }

    internal static bool TryPrepare(out string? error)
    {
        try
        {
            foreach ((MethodInfo target, _) in AllTargets)
            {
                RequireIl(target, PatchProcessor.GetOriginalInstructions(target));
                if (Patcher.FindR2RInliners(target).Count != 0)
                    throw new NotSupportedException("Atlas writer has precompiled inliners: " + target);
            }
            foreach ((MethodInfo target, _) in AllTargets)
                if (!TryDisableInlining(target, out error)) return false;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool TryDisableInlining(MethodBase method, out string? error) =>
        TryDisableInlining(typeof(Harmony).Assembly.GetType("MonoMod.Core.Platforms.PlatformTriple"), method, out error);

    internal static bool TryDisableInlining(Type? platformType, MethodBase method, out string? error)
    {
        try
        {
            object? platform = platformType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            MethodInfo? disable = platformType?.GetMethod("TryDisableInlining", BindingFlags.Public | BindingFlags.Instance, null, [typeof(MethodBase)], null);
            if (platform == null || disable?.ReturnType != typeof(bool) || disable.Invoke(platform, [method]) is not true)
            {
                error = "Installed MonoMod cannot disable future inlining of " + method;
                return false;
            }
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = "MonoMod DisableInlining failed for " + method + ": " + (ex.InnerException ?? ex).Message;
            return false;
        }
    }

    internal static bool TryValidateStartup(long frameCount, object graphics, out string? error)
    {
        if (frameCount != 0)
        {
            error = "Atlas write tracking requires Director.FrameCount == 0";
            return false;
        }
        try
        {
            if (graphics.GetType().FullName != "Halfling.Graphics.D3D11.D3D11GraphicsManager" ||
                AccessTools.Field(graphics.GetType(), "_ownedDisposables")?.GetValue(graphics) is not ConcurrentDictionary<WeakReference<IDisposable>, object?> owned)
            {
                error = "Unknown native graphics ownership registry";
                return false;
            }
            return TryValidateStartup(frameCount, owned.Keys, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool TryValidateStartup(long frameCount, IEnumerable<WeakReference<IDisposable>> owned, out string? error)
    {
        if (frameCount != 0)
        {
            error = "Atlas write tracking requires Director.FrameCount == 0";
            return false;
        }
        foreach (WeakReference<IDisposable> weak in owned)
        {
            if (!weak.TryGetTarget(out IDisposable? resource) || resource is not GraphicsArray<AtlasQuad>) continue;
            error = "Atlas graphics arrays already exist before write tracking";
            return false;
        }
        error = null;
        return true;
    }
}
