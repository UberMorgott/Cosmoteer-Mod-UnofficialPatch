using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;

namespace UnofficialPatch;

/// <summary>Removes boxed comparisons from concrete D3D11 constant updates without patching shared generic code.</summary>
internal static class ShaderConstantBoxing
{
    private const string DirtyIlSha256 = "B3A561CB0EEEEFEF62BC98D4D969FBD897D0703407FED0BF0C764FD6ABC3D870";
    private const string RawDirtyIlSha256 = "409711CB18E0DCA0B70B171DE7A0A2DE8DEDDBA5A65FC1D48994D26ADB874629";
    private const string EqualityIlSha256 = "4C77B6BD9D796D2E40EF0CE35F6F2435FAF5D4CDC2C4F05B719A0431EBB7B081";
    private static readonly (string Name, string Sha256)[] Pins =
    [
        ("Halfling.Geometry.Matrix*", "F3073AB261B96D132A56C6EEDD2B454998C7816F7C58C2A77B8F7B5E15D6B2F3"),
        ("Halfling.Geometry.Vector2", "45085DD4A743FB3F9FC51740051E8B01ABE5872C71BDD5F8106DD61F43F635E3"),
        ("Halfling.Geometry.Vector3", "66EBB59FAD91845F92E7A0360D5042F9B686F861266836B797229434818E84F3"),
        ("Halfling.Geometry.Vector4", "5FAD1217C8E251E790D033A63BECA417BA4D281817072C1C9AF50B527A405826"),
        ("Halfling.Graphics.Color", "B9184C1AF622A52982C1507EC5365729C254DA82A985A238DE43510F89EE9188"),
        ("SharpDX.Mathematics.Interop.RawBool", "395B63572D916CDB36239968A080EF040197E5758CDD34CAE4350D86EF6D37B9"),
        ("System.Int32", "455530AEBE057C3D157C73788F4634C4FA97BC0524696AB74BEEF16648596831"),
        ("System.Single", "0113E0C32E32576294BC4CBA09FC0F875544E7E179C70D57E7F3CD93B08A1E42"),
    ];

    internal static Type ConstantType => Type.GetType("Halfling.Graphics.D3D11.D3D11Shader+D3D11BufferConstant, HalflingPlatformWDX", throwOnError: true)!;

    internal static MethodInfo[] Targets
    {
        get
        {
            MethodInfo[] targets = ConstantType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == "Update").OrderBy(m => m.GetParameters().LastOrDefault()?.ParameterType.FullName, StringComparer.Ordinal).ToArray();
            if (targets.Length != Pins.Length || targets.Any(m => m.IsGenericMethod || m.GetParameters().Length != 2
                || m.GetParameters()[0].ParameterType.FullName != "Halfling.Graphics.D3D11.D3D11GraphicsManager")
                || !targets.Select(m => m.GetParameters()[1].ParameterType.FullName).SequenceEqual(Pins.Select(p => p.Name)))
            {
                throw new MissingMethodException(ConstantType.FullName, "the eight supported non-generic Update overloads");
            }
            return targets;
        }
    }

    private static Type ValueType(MethodInfo method)
    {
        Type parameter = method.GetParameters()[1].ParameterType;
        return parameter.IsPointer ? parameter.GetElementType()! : parameter;
    }

    /// <summary>Object equality, IEquatable dispatch and their shared by-reference implementation for game/SharpDX values.</summary>
    internal static IEnumerable<MethodInfo> EqualityDependencies()
    {
        foreach (Type type in Targets.Select(ValueType).Where(t => t != typeof(int) && t != typeof(float)))
        {
            Type equatable = typeof(IEquatable<>).MakeGenericType(type);
            if (!equatable.IsAssignableFrom(type)) throw new InvalidOperationException(type.FullName + " no longer implements IEquatable<T>");
            yield return AccessTools.Method(type, nameof(Equals), [typeof(object)]);
            yield return type.GetInterfaceMap(equatable).TargetMethods.Single();
            if (AccessTools.Method(type, nameof(Equals), [type.MakeByRefType()]) is { } byRef) yield return byRef;
        }
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        MethodInfo target = Targets.Single(m => m.Module == original.Module && m.MetadataToken == original.MetadataToken);
        string pin = Pins.Single(p => p.Name == target.GetParameters()[1].ParameterType.FullName).Sha256;
        List<CodeInstruction> code = Patcher.RequireIl(instructions, pin, "D3D11BufferConstant.Update(" + target.GetParameters()[1].ParameterType.Name + ")");
        MethodInfo dirty = AccessTools.Method(ConstantType, "IsDataDirty");
        // Pin the open body's original metadata tokens before closing it: ldobj/box/constrained T must not silently
        // become concrete int operations, which would have the same imported listing after substitution with int.
        RequireRawDirtyIl(dirty.GetMethodBody()?.GetILAsByteArray()
            ?? throw new MissingMethodException(ConstantType.FullName, "IsDataDirty IL body"));
        // MonoMod cannot import an open generic body; also inspect the definition closed over int, never patch it.
        Patcher.RequireIl(PatchProcessor.GetOriginalInstructions(dirty.MakeGenericMethod(typeof(int))), DirtyIlSha256, "D3D11BufferConstant.IsDataDirty<int>");
        Patcher.RequireIl(EqualityDependencies().Distinct().SelectMany(m => PatchProcessor.GetOriginalInstructions(m)), EqualityIlSha256, "shader value equality dispatch");
        int call = code.FindIndex(c => c.opcode == OpCodes.Call && c.operand is MethodInfo m && m.IsGenericMethod && m.GetGenericMethodDefinition() == dirty);
        if (call != 2 || code[0].opcode != OpCodes.Ldarg_0 || code.Take(3).Any(c => c.blocks.Count != 0)
            || code.Skip(1).Take(2).Any(c => c.labels.Count != 0))
        {
            throw new InvalidOperationException("Unsupported shader dirty-check call site");
        }
        FieldInfo state = AccessTools.Field(ConstantType, "_bufState");
        FieldInfo offset = AccessTools.Field(ConstantType, "_bufOffset");
        MethodInfo data = AccessTools.PropertyGetter(state.FieldType, "Data");
        MethodInfo compare = AccessTools.Method(typeof(ShaderConstantBoxing), nameof(Dirty)).MakeGenericMethod(ValueType(target));
        // Inline only the receiver-to-buffer pointer expression and replace the boxed comparison. Existing copies/dirty
        // branches remain byte-for-byte intact. Carry every entry label to the first replacement instruction.
        var first = new CodeInstruction(OpCodes.Ldarg_0);
        first.labels.AddRange(code[0].labels);
        var address = new CodeInstruction(code[1]);
        code.RemoveRange(0, 3);
        code.InsertRange(0,
        [
            first, new(OpCodes.Ldfld, state), new(OpCodes.Callvirt, data), new(OpCodes.Ldarg_0), new(OpCodes.Ldfld, offset),
            new(OpCodes.Conv_I), new(OpCodes.Add), address, new(OpCodes.Call, compare),
        ]);
        return code;
    }

    internal static void RequireRawDirtyIl(byte[] bytes)
    {
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (hash != RawDirtyIlSha256)
        {
            throw new InvalidOperationException("D3D11BufferConstant.IsDataDirty open IL changed (sha256 " + hash + ")");
        }
        MethodInfo definition = AccessTools.Method(ConstantType, "IsDataDirty");
        Type parameter = definition.GetGenericArguments().Single();
        // The same TypeSpec token could be repurposed in a new module without changing the body bytes.
        foreach (int operand in new[] { 21, 26, 32 })
        {
            if (definition.Module.ResolveType(BitConverter.ToInt32(bytes, operand), null, [parameter]) != parameter)
                throw new InvalidOperationException("IsDataDirty ldobj/box/constrained operand is no longer its generic T");
        }
    }

    /// <summary>Same value Equals semantics, including NaNs, signed zero and noncanonical RawBool raw integers.</summary>
    public static unsafe bool Dirty<T>(nint data, in T value) where T : unmanaged, IEquatable<T> =>
        !EqualityComparer<T>.Default.Equals(Unsafe.ReadUnaligned<T>((void*)data), value);
}
