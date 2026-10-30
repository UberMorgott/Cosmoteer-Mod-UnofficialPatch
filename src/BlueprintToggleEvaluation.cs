using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Ships.Blueprints;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Logic;
using Cosmoteer.Ships.Parts.UI;
using Cosmoteer.Source.Ships.Blueprints;
using Halfling.Geometry;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Evaluates the exact native UIToggle leaf without a delegate invocation. Every value remains live. Harmony patch
/// installation during a validated update is unsupported: direct calls can be inlined. Patches between updates are
/// checked before another interval opens; outside it the original interface/delegate path is used.
/// </summary>
internal static class BlueprintToggleEvaluation
{
    [ThreadStatic] private static bool _validatedUpdate;
    private static MethodBase[]? _dependencies;
    private static bool _nativeValidated;
    private static bool _nativeRejected;
    private static bool _loggedActive;
    private static bool _loggedRejected;

    internal static bool IsValidatedUpdate => _validatedUpdate;

    public static void BeginValidatedUpdate()
    {
        _validatedUpdate = false;
        if (_nativeRejected) return;
        try
        {
            if (!_nativeValidated) ValidateNativeDependencies();
            // Enumerate patched methods once, rather than allocating a patch-info snapshot for every unpatched getter.
            foreach (MethodBase patched in Harmony.GetAllPatchedMethods())
            {
                if (!IsReadDependency(patched)) continue;
                Patches? patches = Harmony.GetPatchInfo(patched);
                if (patches == null) throw new InvalidOperationException("Patch information disappeared for " + patched);
                foreach (string owner in patches.Owners)
                {
                    if (owner == Patcher.HarmonyId) continue;
                    if (!_loggedRejected) Reject("foreign Harmony owner " + owner + " on " + patched.DeclaringType?.FullName + "." + patched.Name);
                    return;
                }
            }
            _validatedUpdate = true;
        }
        catch (Exception ex)
        {
            _nativeRejected = true;
            Reject(ex.Message);
        }
    }

    public static void EndValidatedUpdate() => _validatedUpdate = false;

    internal static bool SameMethodDefinition(MethodBase a, MethodBase b) =>
        a == b || (a.Module == b.Module && a.MetadataToken == b.MetadataToken);

    private static bool IsReadDependency(MethodBase patched)
    {
        foreach (MethodBase dependency in _dependencies!)
            if (SameMethodDefinition(dependency, patched)) return true;
        return false;
    }

    private static void Reject(string reason)
    {
        if (_loggedRejected) return;
        _loggedRejected = true;
        Log.Info("Blueprint toggle specialization disabled: " + reason);
    }

    public static bool Evaluate(BaseBlueprintPartNetworkPort owner)
    {
        IBlueprintComponentToggle? source = owner._rulesOperationalToggle;
        // The original evaluates Part.Rules before creating the delegate and invoking the interface, even for null source.
        PartRules rules = owner.Part!.Rules;
        if (_validatedUpdate && source?.GetType() == typeof(PartUIToggleRules))
        {
            var leaf = (PartUIToggleRules)source;
            int mode = owner.GetToggleMode(leaf.ToggleID) ?? leaf.Default;
            bool result = leaf.ToggledOnChoices?.Contains(mode) ?? mode != 0;
            if (!_loggedActive)
            {
                _loggedActive = true;
                Log.Info("Blueprint toggle specialization active: exact live UIToggle evaluation");
            }
            return result;
        }
        return source!.IsBlueprintToggleOn(rules, PortToggleMode.Get(owner, 0));
    }

    internal static void ValidateNativeDependencies()
    {
        MethodBase[] dependencies = _dependencies ??= ReadDependencies();
        Patcher.RequireIl(dependencies.SelectMany(m => PatchProcessor.GetOriginalInstructions(m)),
            "807156E42F0F57C6C91D270D3B98A86F2BE1188D3770737AA721CEBAE501106A", "Blueprint UIToggle evaluation dependencies");
        _nativeValidated = true;
    }

    private static MethodBase[] ReadDependencies()
    {
        var methods = new List<MethodBase>();
        var pending = new Queue<MethodBase>();
        pending.Enqueue(PortToggleMode.ToggleModes[0]);
        pending.Enqueue(AccessTools.Method(typeof(PartUIToggleRules), nameof(PartUIToggleRules.IsBlueprintToggleOn)));
        // Dictionary dispatches struct comparers indirectly, so those native dependencies are explicit roots.
        foreach (Type type in new[] { typeof(PartSaveData), typeof(ID<PartRules>), typeof(ID<PartToggleGuiRules>), typeof(IntVector2) })
        {
            pending.Enqueue(AccessTools.Method(type, nameof(GetHashCode), Type.EmptyTypes));
            // Dictionary's comparer dispatches IEquatable<T>, which can differ from the public Equals(in T) overload.
            InterfaceMapping comparer = type.GetInterfaceMap(typeof(IEquatable<>).MakeGenericType(type));
            foreach (MethodInfo implementation in comparer.TargetMethods) pending.Enqueue(implementation);
        }
        while (pending.Count != 0)
        {
            MethodBase method = pending.Dequeue();
            if (methods.Contains(method)) continue;
            if (method.GetMethodBody() == null) throw new InvalidOperationException("Unreadable native dependency " + method);
            methods.Add(method);
            foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(method))
            {
                if (instruction.operand is not MethodBase called ||
                    (instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt && instruction.opcode != OpCodes.Newobj)) continue;
                string? assembly = called.DeclaringType?.Assembly.GetName().Name;
                if (assembly is "Cosmoteer" or "HalflingCore") pending.Enqueue(called);
            }
        }
        // Part.Rules is evaluated before the leaf and can itself be Harmony-patched/inlined.
        methods.Add(AccessTools.PropertyGetter(typeof(BlueprintPartComponent), nameof(BlueprintPartComponent.Part)));
        methods.Add(AccessTools.PropertyGetter(typeof(CommonBasePart), nameof(CommonBasePart.Rules)));
        return methods.Distinct().ToArray();
    }
}
