using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Ships.Rendering;
using Halfling.Graphics;
using HarmonyLib;

namespace Performance;

internal static class AtlasDrawClone
{
    private static readonly Type ArrayType = typeof(GraphicsArray<AtlasQuad>);
    private static readonly Type ListType = typeof(GraphicsList<AtlasQuad>);
    private static readonly Type CallbackType = typeof(Action<GraphicsArray<AtlasQuad>, GraphicsList<AtlasQuad>, int, int>);
    private static readonly Dictionary<string, OpCode> LongBranches = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
        .Where(o => o.OperandType == OperandType.InlineBrTarget).ToDictionary(o => o.Name!, StringComparer.Ordinal);

    // This factory preserves a closed body mechanically. Native version/patch eligibility belongs to its caller.
    internal static Action<object, GraphicsArray<AtlasQuad>, int, int, GraphicsList<AtlasQuad>> Create(
        MethodInfo source, MethodInfo upload, Action<GraphicsArray<AtlasQuad>, GraphicsList<AtlasQuad>, int, int> callback)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(callback);
        Type backend = source.DeclaringType ?? throw new NotSupportedException("Missing backend type");
        Type receiver = upload.DeclaringType ?? throw new NotSupportedException("Missing upload receiver type");
        MethodBody body = source.GetMethodBody() ?? throw new NotSupportedException("Missing source body");
        if (source.IsStatic || source.ReturnType != typeof(void) || source.ContainsGenericParameters || backend.IsValueType ||
            backend.ContainsGenericParameters || (source.CallingConvention & CallingConventions.VarArgs) == CallingConventions.VarArgs ||
            !source.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { ArrayType, typeof(int), typeof(int) }) ||
            body.ExceptionHandlingClauses.Count != 0 || upload.IsStatic || upload.ReturnType != typeof(void) ||
            upload.ContainsGenericParameters || receiver.ContainsGenericParameters || !ArrayType.IsAssignableFrom(receiver) || upload.GetParameters().Length != 0)
            throw new NotSupportedException("Unsupported closed draw/upload shape");

        Type[] parameters = [backend, ArrayType, typeof(int), typeof(int), ListType, CallbackType];
        var clone = new DynamicMethod("OwnedAtlasDraw", typeof(void), parameters, typeof(AtlasDrawClone).Module, true) { InitLocals = body.InitLocals };
        ILGenerator il = clone.GetILGenerator();
        List<CodeInstruction> codes = PatchProcessor.GetOriginalInstructions(source, il);
        var matches = codes.Where(c => c.operand is MethodInfo method && ReferenceEquals(method.Module, upload.Module) && method.Equals(upload)).ToArray();
        if (matches.Length != 1 || matches[0].opcode != OpCodes.Callvirt)
            throw new NotSupportedException("Expected exactly one instance callvirt upload");
        // Harmony has already declared all original locals on this generator; append ours afterwards.
        LocalBuilder savedReceiver = il.DeclareLocal(receiver);
        foreach (CodeInstruction code in codes)
        {
            Validate(code);
            foreach (Label label in code.labels) il.MarkLabel(label);
            if (ReferenceEquals(code, matches[0]))
            {
                il.Emit(OpCodes.Stloc, savedReceiver);
                il.Emit(OpCodes.Ldloc, savedReceiver);
                ThrowIfNull(il); // Preserve the replaced callvirt's null check before invoking user code.
                il.Emit(OpCodes.Ldarg_S, (byte)5);
                il.Emit(OpCodes.Ldloc, savedReceiver);
                il.Emit(OpCodes.Ldarg_S, (byte)4);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Callvirt, CallbackType.GetMethod("Invoke")!);
            }
            else
            {
                OpCode opcode = code.opcode.OperandType == OperandType.ShortInlineBrTarget ? LongBranches[code.opcode.Name![..^2]] : code.opcode;
                Emit(il, opcode, code.operand);
            }
        }
        // Prepare the typed body too, not merely the object adapter. Neither delegate is executed here.
        RuntimeHelpers.PrepareDelegate(clone.CreateDelegate(Expression.GetDelegateType([.. parameters, typeof(void)])));
        var adapter = new DynamicMethod("OwnedAtlasDrawAdapter", typeof(void),
            [CallbackType, typeof(object), ArrayType, typeof(int), typeof(int), ListType], typeof(AtlasDrawClone).Module, true);
        ILGenerator bridge = adapter.GetILGenerator();
        bridge.Emit(OpCodes.Ldarg_1);
        bridge.Emit(OpCodes.Castclass, backend);
        bridge.Emit(OpCodes.Dup);
        ThrowIfNull(bridge); // An explicit-this static clone must retain instance callvirt null semantics.
        bridge.Emit(OpCodes.Ldarg_2);
        bridge.Emit(OpCodes.Ldarg_3);
        bridge.Emit(OpCodes.Ldarg_S, (byte)4);
        bridge.Emit(OpCodes.Ldarg_S, (byte)5);
        bridge.Emit(OpCodes.Ldarg_0);
        bridge.Emit(OpCodes.Call, clone);
        bridge.Emit(OpCodes.Ret);
        return (Action<object, GraphicsArray<AtlasQuad>, int, int, GraphicsList<AtlasQuad>>)adapter.CreateDelegate(
            typeof(Action<object, GraphicsArray<AtlasQuad>, int, int, GraphicsList<AtlasQuad>>), callback);
    }

    private static void ThrowIfNull(ILGenerator il)
    {
        Label present = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, present);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(present);
    }

    private static void Validate(CodeInstruction code)
    {
        if (code.blocks.Count != 0 || code.opcode == OpCodes.Calli || code.opcode == OpCodes.Jmp ||
            code.opcode.OpCodeType == OpCodeType.Prefix ||
            code.operand is Type type && type.ContainsGenericParameters ||
            code.operand is MemberInfo member && member.DeclaringType?.ContainsGenericParameters == true ||
            code.operand is MethodInfo method && method.ContainsGenericParameters ||
            code.operand is LocalBuilder local && local.LocalType.ContainsGenericParameters)
            throw new NotSupportedException("Unsupported source instruction: " + code);
    }

    private static void Emit(ILGenerator il, OpCode opcode, object? operand)
    {
        switch (operand)
        {
            case null: il.Emit(opcode); break;
            case LocalBuilder local: il.Emit(opcode, local); break;
            case Label label: il.Emit(opcode, label); break;
            case Label[] labels: il.Emit(opcode, labels); break;
            case MethodInfo method: il.Emit(opcode, method); break;
            case ConstructorInfo constructor: il.Emit(opcode, constructor); break;
            case FieldInfo field: il.Emit(opcode, field); break;
            case Type type: il.Emit(opcode, type); break;
            case string text: il.Emit(opcode, text); break;
            case byte value: il.Emit(opcode, value); break;
            case sbyte value: il.Emit(opcode, value); break;
            case short value: il.Emit(opcode, value); break;
            case int value when opcode.OperandType == OperandType.ShortInlineVar: il.Emit(opcode, checked((byte)value)); break;
            case int value when opcode.OperandType == OperandType.InlineVar: il.Emit(opcode, checked((short)value)); break;
            case int value: il.Emit(opcode, value); break;
            case long value: il.Emit(opcode, value); break;
            case float value: il.Emit(opcode, value); break;
            case double value: il.Emit(opcode, value); break;
            default: throw new NotSupportedException("Unsupported source operand: " + operand.GetType().FullName);
        }
    }
}
