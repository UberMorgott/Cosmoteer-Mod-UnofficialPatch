using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Game;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Rendering;
using Cosmoteer.Ships.Special;
using Halfling.Input;
using HarmonyLib;
using Xunit;

namespace UnofficialPatch.Tests;

public class RoofOpacityTests
{
    [Fact]
    public void MovesOnlyTheInputCheckBeforeCursorLookup()
    {
        var original = PatchProcessor.GetOriginalInstructions(RoofOpacity.Target);
        var patched = RoofOpacity.Transpiler(original).ToList();
        int key = patched.FindIndex(c => c.opcode == OpCodes.Ldsfld && c.operand is FieldInfo { Name: "HideShipRoofUnderCursor" });
        int cursor = patched.FindIndex(c => c.operand is MethodInfo { Name: "GetShipUnderCursor" });
        Assert.True(key < cursor, "The released key must short-circuit the cursor query.");
        Assert.Equal(original.Count, patched.Count);
        Assert.Equal(OpCodes.Brfalse_S, patched[key + 2].opcode);
        CodeInstruction fallback = patched.Single(c => c.labels.Contains((Label)patched[cursor + 3].operand));
        Assert.Contains((Label)patched[key + 2].operand, fallback.labels);
        // Every instruction, label and exception boundary is retained; only the three-input-instruction block moves.
        static string Show(CodeInstruction c) => $"{c} [{string.Join(",", c.labels)}] [{string.Join(",", c.blocks)}]";
        Assert.Equal(original.Select(Show).Order(), patched.Select(Show).Order());
    }

    [Fact]
    public void RejectsChangedGameIl()
    {
        var code = PatchProcessor.GetOriginalInstructions(RoofOpacity.Target);
        code.Insert(0, new CodeInstruction(OpCodes.Nop));
        Assert.Throws<InvalidOperationException>(() => RoofOpacity.Transpiler(code).ToList());
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void PreservesResultsAndSkipsReleasedKeyHitTests(string cultureName)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(cultureName);
        try
        {
            var vanilla = Compile(patched: false);
            var patched = Compile(patched: true);
            foreach (bool? forced in new bool?[] { null, false, true })
                foreach (int special in Enumerable.Range(0, 4)) // absent, defer to vanilla, show interior, hide interior
                    foreach (int bits in Enumerable.Range(0, 128)) // game, toolbox, key, roofs, selection, cursor, selection rectangle
                        foreach (bool differentShip in new[] { false, true })
                        {
                            _case = new RoofCase(forced, special, bits, differentShip);
                            float expected = vanilla(null!);
                            int originalQueries = _case.CursorQueries;
                            _case.CursorQueries = 0;
                            Assert.Equal(expected, patched(null!));
                            Assert.Equal(_case.KeyDown ? originalQueries : 0, _case.CursorQueries);
                        }
        }
        finally
        {
            _case = null!;
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static RoofCase _case = null!;

    private sealed class Sight : ISpecialSightProvider { }

    private sealed class RoofCase
    {
        private static readonly Ship ThisShip = (Ship)RuntimeHelpers.GetUninitializedObject(typeof(Ship));
        private static readonly Ship OtherShip = (Ship)RuntimeHelpers.GetUninitializedObject(typeof(Ship));
        private static readonly GameRoot Game = (GameRoot)RuntimeHelpers.GetUninitializedObject(typeof(GameRoot));
        private static readonly Sight Provider = new();
        private readonly int _bits;
        private readonly int _special;
        private readonly bool _differentShip;
        private readonly Dictionary<Ship, bool> _forced = new();
        public int CursorQueries;
        public bool KeyDown => (_bits & 4) != 0;

        public RoofCase(bool? forced, int special, int bits, bool differentShip)
        {
            _bits = bits;
            _special = special;
            _differentShip = differentShip;
            if (forced.HasValue)
            {
                _forced.Add(ThisShip, forced.Value);
            }
        }

        public object? Read(string member)
        {
            if (member == "GetShipUnderCursor")
            {
                CursorQueries++;
                return (_bits & 32) == 0 || (_bits & 64) != 0 ? null : _differentShip ? OtherShip : ThisShip;
            }
            return member switch
            {
                "get_ForceShowRoofs" => _forced,
                "get_Ship" => ThisShip,
                "get_Game" => (_bits & 1) != 0 ? Game : null,
                "get_SpecialSightProvider" => _special == 0 ? null : Provider,
                "ShowInterior" => _special < 2 ? null : _special == 2,
                "get_HasOpenToolbox" => (_bits & 2) != 0,
                "get_IsDown" => KeyDown,
                "get_ShowRoofs" => (_bits & 8) != 0,
                "get_IsSelected" => (_bits & 16) != 0,
                _ => null, // intermediate scene receivers are ignored by the next substituted getter
            };
        }
    }

    private static T Read<T>(string member) => (T)_case.Read(member)!;

    /// <summary>
    /// Executes the real game's complete roof method and its real branches/nullable/dictionary operations. Only scene
    /// getters, the polled input and cursor query are substituted so every early return can run without a GPU/window.
    /// </summary>
    private static Func<ShipRenderer, float> Compile(bool patched)
    {
        var method = new DynamicMethod("RoofOpacityCase", typeof(float), [typeof(ShipRenderer)], typeof(RoofOpacityTests), skipVisibility: true);
        ILGenerator generator = method.GetILGenerator();
        // Harmony declares the original locals on this generator while reading the body.
        var source = PatchProcessor.GetOriginalInstructions(RoofOpacity.Target, generator);
        foreach (CodeInstruction c in patched ? RoofOpacity.Transpiler(source) : source)
        {
            Assert.Empty(c.blocks);
            foreach (Label label in c.labels)
            {
                generator.MarkLabel(label);
            }
            if (c.opcode == OpCodes.Ldsfld && c.operand is FieldInfo { Name: "HideShipRoofUnderCursor" })
            {
                generator.Emit(OpCodes.Ldnull); // avoid initializing the application's input manager
            }
            else if (c.operand is MethodInfo m && (m.DeclaringType!.Assembly == typeof(Ship).Assembly || m.DeclaringType == typeof(InputButton)))
            {
                for (int n = 0; n < m.GetParameters().Length + (m.IsStatic ? 0 : 1); n++)
                {
                    generator.Emit(OpCodes.Pop);
                }
                generator.Emit(OpCodes.Ldstr, m.Name);
                generator.Emit(OpCodes.Call, AccessTools.Method(typeof(RoofOpacityTests), nameof(Read)).MakeGenericMethod(m.ReturnType));
            }
            else
            {
                switch (c.operand)
                {
                    case null: generator.Emit(c.opcode); break;
                    case MethodInfo called: generator.Emit(c.opcode, called); break;
                    case Type t: generator.Emit(c.opcode, t); break;
                    case Label label: generator.Emit(c.opcode, label); break;
                    case LocalBuilder local: generator.Emit(c.opcode, local); break;
                    case byte b: generator.Emit(c.opcode, b); break;
                    case float f: generator.Emit(c.opcode, f); break;
                    default: throw new InvalidOperationException("Unexpected roof IL operand: " + c);
                }
            }
        }
        return method.CreateDelegate<Func<ShipRenderer, float>>();
    }
}
