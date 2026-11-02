using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Halfling.Collections;
using Halfling.Pooling;
using Halfling.Scene2D;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Every scene component that stops, starts or is removed (sounds, media effects, beam effects, continuous part effects,
/// space crew...) takes itself out of the scene root's update / fixed-update / input bucket lists with
/// <c>BucketList.Remove(bucket, this)</c>. That is <c>List.Remove</c>: a linear <c>IndexOf</c> that calls the virtual
/// <c>Equals</c> of every element in front of it, so each element's object is loaded from memory. In a 40-ship battle
/// the update buckets hold thousands of components and this was about 9% of the main thread.
/// <para/>
/// The four SceneComponent methods that do this now call <see cref="Remove{T}"/>, which finds the same element by
/// reference comparison only (no element is dereferenced) and removes it with the same <c>RemoveAt</c>, so the list order,
/// the bucket bookkeeping and the result are unchanged. Reference comparison equals vanilla's <c>Equals</c> only while no
/// object in these lists overrides <c>Equals(object)</c>: every type of every loaded static assembly is checked (also
/// assemblies loaded later), and the runtime type of every object the three ReAdd methods put into a list (the game's
/// only adders) is checked too, which covers types emitted at runtime. Any such type, or an assembly that cannot be
/// inspected, switches the fast path off for the rest of the session.
/// shortcut: foreign code adding an Equals-overriding runtime-emitted type straight to SceneRoot.Updateables is not seen.
/// Off: <c>PERFORMANCE_SCENE_BUCKET_REMOVE=0</c>.
/// </summary>
internal static class SceneBucketRemove
{
    internal static readonly bool Requested = IsRequested(Environment.GetEnvironmentVariable("PERFORMANCE_SCENE_BUCKET_REMOVE"));
    internal static bool IsRequested(string? value) => value != "0";

    /// <summary>Target method name, IL sha256, BucketList.Remove and BucketList.Add call sites.</summary>
    private static readonly (string Name, string Sha256, int Removes, int Adds)[] Methods =
    [
        ("OnDeactivated", "D8230E14A2DDD7DE860D33768E55EE815874D16D5AF638B7ABE59B37143C753A", 3, 0),
        ("ReAddUpdate", "CDAF35936CFBB70DE88BC005B83DAB75F3ACE9FEAF85A4007D9AD5C0F960F461", 1, 1),
        ("ReAddFixedUpdate", "58E9A1E7F88F4D31EF14FF0729DE46CA714C9F3DBBCDF3F70D1B735B6CA27900", 1, 1),
        ("ReAddInput", "1670B15FF591A2B4873C8A6DFE4FBFD9466442CC39CB37A756F50506AB9EFD2C", 1, 1),
    ];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> CheckedTypes = new();

    internal static readonly MethodInfo[] Targets = Methods.Select(m => AccessTools.Method(typeof(SceneComponent), m.Name, Type.EmptyTypes)
        ?? throw new MissingMethodException(nameof(SceneComponent), m.Name)).ToArray();

    internal static (MethodBase Target, Type Patch)[] Patches => Requested ? Targets.Select(t => ((MethodBase)t, typeof(SceneBucketRemove))).ToArray() : [];

    private static readonly Type[] ListedInterfaces = [typeof(IUpdateableSceneObject), typeof(IFixedUpdateableSceneObject), typeof(IInputableSceneObject)];

    private static volatile bool _referenceEquality;

    /// <summary>Fast path on only while no listable type overrides Equals; rechecked for every assembly loaded later.</summary>
    internal static void Initialize()
    {
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) => CheckAssembly(e.LoadedAssembly);
        _referenceEquality = true;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) CheckAssembly(assembly);
        if (!_referenceEquality) Log.Info("SceneBucketRemove: a scene object type overrides Equals; bucket removal stays vanilla.");
    }

    internal static void Disable() => _referenceEquality = false;

    private static void CheckAssembly(Assembly assembly)
    {
        if (!_referenceEquality || assembly.IsDynamic) return; // emitted types are checked when they are added (AddChecked)
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types; // types that failed to load cannot be instantiated
        }
        catch (Exception ex)
        {
            Log.Info($"SceneBucketRemove: cannot inspect {assembly.FullName} ({ex.GetType().Name}); bucket removal stays vanilla.");
            _referenceEquality = false;
            return;
        }
        foreach (Type? type in types)
        {
            if (type != null && OverridesEquals(type))
            {
                Log.Info($"SceneBucketRemove: {type.FullName} overrides Equals; bucket removal stays vanilla.");
                _referenceEquality = false;
                return;
            }
        }
    }

    /// <summary>A type that can be stored in one of the three bucket lists and whose Equals(object) is not object's.</summary>
    internal static bool OverridesEquals(Type type)
    {
        try
        {
            if (type.IsInterface || !ListedInterfaces.Any(i => i.IsAssignableFrom(type))) return false;
            MethodInfo? equals = type.GetMethod(nameof(Equals), BindingFlags.Public | BindingFlags.Instance, null, [typeof(object)], null);
            return equals == null || equals.DeclaringType != typeof(object);
        }
        catch (Exception)
        {
            return true; // unknown: stay vanilla
        }
    }

    /// <summary>Replaces each <c>BucketList&lt;T&gt;.Remove(int, T)</c> call with <see cref="Remove{T}"/>, each Add with <see cref="AddChecked{T}"/>.</summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        (string name, string sha256, int removes, int adds) = Methods.Single(m => m.Name == original.Name);
        List<CodeInstruction> code = Patcher.RequireIl(instructions, sha256, "SceneComponent." + name);
        int removed = 0, added = 0;
        foreach (CodeInstruction ci in code)
        {
            // callvirt MultiDictionaryBase<int, T>.Remove/Add(int, T) on SceneRoot.Updateables / FixedUpdateables / Inputables (BucketList<T>).
            if (ci.operand is not MethodInfo { Name: "Remove" or "Add" } m || m.DeclaringType is not { IsGenericType: true } owner
                || owner.GetGenericTypeDefinition() != typeof(MultiDictionaryBase<,>) || m.GetParameters().Length != 2) continue;
            Type item = owner.GetGenericArguments()[1];
            if (!ListedInterfaces.Contains(item)) throw new InvalidOperationException($"SceneComponent.{name}: unexpected bucket item {item}");
            bool remove = m.Name == "Remove";
            ci.opcode = OpCodes.Call;
            ci.operand = AccessTools.Method(typeof(SceneBucketRemove), remove ? nameof(Remove) : nameof(AddChecked)).MakeGenericMethod(item);
            if (remove) removed++;
            else added++;
        }
        if (removed != removes || added != adds) throw new InvalidOperationException($"SceneComponent.{name}: {removed} BucketList.Remove / {added} Add calls, expected {removes} / {adds}");
        return code;
    }

    /// <summary>Vanilla <c>BucketList.Add</c>; an object whose runtime type overrides Equals turns the fast path off first.</summary>
    internal static void AddChecked<T>(BucketList<T> buckets, int bucket, T value) where T : class
    {
        if (_referenceEquality && CheckedTypes.GetOrAdd(value.GetType(), OverridesEquals))
        {
            _referenceEquality = false;
            Log.Info($"SceneBucketRemove: {value.GetType().FullName} overrides Equals; bucket removal stays vanilla.");
        }
        buckets.Add(bucket, value);
    }
    /// <summary>Vanilla <c>BucketList.Remove(bucket, value)</c>, the element found by reference.</summary>
    internal static bool Remove<T>(BucketList<T> buckets, int bucket, T value) where T : class
    {
        // A one-element bucket is dropped by vanilla itself (cheap); a foreign Equals keeps vanilla. Callers pass `this` (never null).
        if (!_referenceEquality || !buckets._bucketsByKey.TryGetValue(bucket, out TempList<T>? list) || list.Count <= 1)
            return buckets.Remove(bucket, value);
        Span<T> items = CollectionsMarshal.AsSpan(list);
        for (int i = 0; i < items.Length; i++)
        {
            if (!ReferenceEquals(items[i], value)) continue;
            list.RemoveAt(i);
            buckets._count--;
            return true;
        }
        return false;
    }
}
