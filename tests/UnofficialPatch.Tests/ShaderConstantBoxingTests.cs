using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Xunit;

namespace UnofficialPatch.Tests;

public class ShaderConstantBoxingTests
{
    public static TheoryData<int> TargetIndices => new(Enumerable.Range(0, 8));

    [Fact]
    public void OpenGenericTokensCannotBeReplacedByConcreteIntTokens()
    {
        MethodInfo dirty = AccessTools.Method(ShaderConstantBoxing.ConstantType, "IsDataDirty");
        Assert.True(dirty.IsGenericMethodDefinition);
        byte[] original = dirty.GetMethodBody()!.GetILAsByteArray()!;
        ShaderConstantBoxing.RequireRawDirtyIl(original);
        using FileStream stream = File.OpenRead(dirty.Module.Assembly.Location);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        TypeReferenceHandle intReference = metadata.TypeReferences.Single(h =>
            metadata.GetString(metadata.GetTypeReference(h).Namespace) == "System"
            && metadata.GetString(metadata.GetTypeReference(h).Name) == "Int32");
        int intToken = MetadataTokens.GetToken(intReference);
        byte[] changed = original.ToArray();
        // These are the three T operands in the pinned 45-byte helper: ldobj T, box T and constrained T.
        Assert.Equal((byte)OpCodes.Ldobj.Value, original[20]);
        Assert.Equal((byte)OpCodes.Box.Value, original[25]);
        Assert.Equal((byte)0xfe, original[30]);
        Assert.Equal((byte)0x16, original[31]);
        foreach (int operand in new[] { 21, 26, 32 })
        {
            int genericToken = BitConverter.ToInt32(original, operand);
            Assert.True(dirty.Module.ResolveType(genericToken, null, dirty.GetGenericArguments()).IsGenericParameter);
            // Importing either operand after closing with int hides the distinction that matters for other seven Ts.
            Assert.Equal(dirty.Module.ResolveType(intToken), dirty.Module.ResolveType(genericToken, null, [typeof(int)]));
            Assert.True(BitConverter.TryWriteBytes(changed.AsSpan(operand, sizeof(int)), intToken));
        }
        Assert.Throws<InvalidOperationException>(() => ShaderConstantBoxing.RequireRawDirtyIl(changed));
    }

    [Theory]
    [MemberData(nameof(TargetIndices))]
    public void PatchedCallHasNoBoxedGenericDispatchAndRejectsChangedIl(int index)
    {
        MethodInfo target = ShaderConstantBoxing.Targets[index];
        var original = PatchProcessor.GetOriginalInstructions(target);
        var patched = ShaderConstantBoxing.Transpiler(original, target).ToList();
        Assert.False(target.IsGenericMethod);
        Assert.DoesNotContain(patched, c => c.operand is MethodInfo { Name: "IsDataDirty" });
        original = PatchProcessor.GetOriginalInstructions(target);
        original.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => ShaderConstantBoxing.Transpiler(original, target).ToList());
    }

    [Theory]
    [MemberData(nameof(TargetIndices))]
    public void RealUpdatePreservesBufferAndDirtyState(int index)
    {
        MethodInfo target = ShaderConstantBoxing.Targets[index];
        Action<object, nint> vanilla = Compile(target, patched: false), patched = Compile(target, patched: true);
        // All 32-bit words are tested, including every matrix component. Float and RawBool interpretations both matter.
        uint[] values = [0, 0x80000000, 1, 2, uint.MaxValue, 0x3f800000, 0x40000000, 0x7f800000, 0xff800000, 0x7fc00001, 0x7fc00002, 0xffc00001];
        foreach (uint before in values)
            foreach (uint input in values)
                foreach (int offset in new[] { 16, 17 })
                    foreach (bool alreadyDirty in new[] { false, true })
                    {
                        using var a = new Buffer(target, offset);
                        using var b = new Buffer(target, offset);
                        a.Fill(before, input, alreadyDirty);
                        b.Fill(before, input, alreadyDirty);
                        vanilla(a.Owner, a.Input);
                        patched(b.Owner, b.Input);
                        Assert.Equal(a.Read(), b.Read());
                        Assert.Equal(a.IsDirty, b.IsDirty);
                    }
    }

    [Theory]
    [MemberData(nameof(TargetIndices))]
    public void EveryIndividualComponentCanDirtyTheBuffer(int index)
    {
        MethodInfo target = ShaderConstantBoxing.Targets[index];
        Action<object, nint> vanilla = Compile(target, patched: false), patched = Compile(target, patched: true);
        foreach (int offset in new[] { 16, 17 })
        {
            using var a = new Buffer(target, offset);
            using var b = new Buffer(target, offset);
            for (int word = 0; word < a.Words; word++)
            {
                a.Fill(0, 0, alreadyDirty: false);
                b.Fill(0, 0, alreadyDirty: false);
                a.SetInputWord(word, 0x3f800000);
                b.SetInputWord(word, 0x3f800000);
                vanilla(a.Owner, a.Input);
                patched(b.Owner, b.Input);
                Assert.True(a.IsDirty);
                Assert.True(b.IsDirty);
                Assert.Equal(a.Read(), b.Read());
            }
        }
    }

    [Theory]
    [MemberData(nameof(TargetIndices))]
    public void PatchedRealUpdateAllocatesNothingPerCall(int index)
    {
        MethodInfo target = ShaderConstantBoxing.Targets[index];
        Action<object, nint> run = Compile(target, patched: true);
        using var buffer = new Buffer(target, 16);
        buffer.Fill(0, 0, alreadyDirty: false);
        for (int i = 0; i < 1000; i++) run(buffer.Owner, buffer.Input);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100_000; i++) run(buffer.Owner, buffer.Input);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 256);
        buffer.SetInputWord(0, 0);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100_000; i++)
        {
            buffer.SetInputWord(0, (i & 1) == 0 ? 0x3f800000u : 0);
            run(buffer.Owner, buffer.Input);
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 256);
    }

    /// <summary>Runs the real Update body with native input bytes and a headless buffer, including its actual writes.</summary>
    private static Action<object, nint> Compile(MethodInfo target, bool patched)
    {
        var method = new DynamicMethod("ShaderConstantUpdate", typeof(void), [typeof(object), typeof(nint)], typeof(ShaderConstantBoxingTests).Module, skipVisibility: true);
        ILGenerator il = method.GetILGenerator();
        var source = PatchProcessor.GetOriginalInstructions(target, il);
        Type parameter = target.GetParameters()[1].ParameterType;
        foreach (CodeInstruction c in patched ? ShaderConstantBoxing.Transpiler(source, target) : source)
        {
            Assert.Empty(c.blocks);
            foreach (Label label in c.labels) il.MarkLabel(label);
            if (c.opcode == OpCodes.Ldarg_0)
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, target.DeclaringType!);
            }
            else if (c.opcode == OpCodes.Ldarg_2)
            {
                il.Emit(OpCodes.Ldarg_1);
                if (!parameter.IsPointer) il.Emit(OpCodes.Ldobj, parameter);
            }
            else if (c.opcode == OpCodes.Ldarga_S)
            {
                il.Emit(OpCodes.Ldarg_1);
            }
            else
            {
                switch (c.operand)
                {
                    case null: il.Emit(c.opcode); break;
                    case MethodInfo m: il.Emit(c.opcode, m); break;
                    case FieldInfo f: il.Emit(c.opcode, f); break;
                    case Label l: il.Emit(c.opcode, l); break;
                    case Type t: il.Emit(c.opcode, t); break;
                    default: throw new InvalidOperationException("Unexpected Update IL: " + c);
                }
            }
        }
        return method.CreateDelegate<Action<object, nint>>();
    }

    private sealed class Buffer : IDisposable
    {
        private const int Capacity = 128;
        private readonly nint _data = Marshal.AllocHGlobal(Capacity);
        private readonly object _state;
        private readonly int _offset;
        private readonly int _size;
        private readonly PropertyInfo _dirty;
        public readonly object Owner;
        public readonly nint Input = Marshal.AllocHGlobal(64);
        public bool IsDirty => (bool)_dirty.GetValue(_state)!;
        public int Words => _size / 4;

        public void SetInputWord(int word, uint value) => Marshal.WriteInt32(Input + word * 4, unchecked((int)value));

        public Buffer(MethodInfo target, int offset)
        {
            _offset = offset;
            Type valueType = target.GetParameters()[1].ParameterType;
            if (valueType.IsPointer) valueType = valueType.GetElementType()!;
            _size = Marshal.SizeOf(valueType);
            Owner = RuntimeHelpers.GetUninitializedObject(target.DeclaringType!);
            FieldInfo state = AccessTools.Field(target.DeclaringType, "_bufState");
            _state = RuntimeHelpers.GetUninitializedObject(state.FieldType);
            AccessTools.Field(state.FieldType, "_data").SetValue(_state, _data);
            state.SetValue(Owner, _state);
            AccessTools.Field(target.DeclaringType, "_bufOffset").SetValue(Owner, offset);
            _dirty = AccessTools.Property(state.FieldType, "IsDirty");
        }

        public void Fill(uint before, uint input, bool alreadyDirty)
        {
            Marshal.Copy(Enumerable.Repeat((byte)0xa5, Capacity).ToArray(), 0, _data, Capacity);
            for (int i = 0; i < _size; i += 4)
            {
                Marshal.WriteInt32(_data + _offset + i, unchecked((int)before));
                Marshal.WriteInt32(Input + i, unchecked((int)input));
            }
            _dirty.SetValue(_state, alreadyDirty);
        }

        public byte[] Read()
        {
            var bytes = new byte[Capacity];
            Marshal.Copy(_data, bytes, 0, bytes.Length);
            return bytes;
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Input);
            Marshal.FreeHGlobal(_data);
        }
    }
}
