using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Cosmoteer;
using Cosmoteer.Data;
using Cosmoteer.Factions;
using Cosmoteer.GalaxyMap;
using Cosmoteer.Localization;
using Cosmoteer.Modes.Career;
using Cosmoteer.Modes.Career.Map;
using Halfling;
using HarmonyLib;

namespace Performance;

/// <summary>
/// Vanilla CareerGameModeManager.GetMapNodeText rebuilds every galaxy-map label each frame (escaped name, tier markup,
/// faction and sector lines: ~10 MB/s in an MP profile). UI only, never sim. Prefix/postfix cache the text this method
/// appends, per node, keyed on every input it reads: node name, TierRange and SectorTypeID of its MapNodeData, the
/// controlling faction (live from faction strengths), and the GameApp.Rules instance (faction icon/name, sector name, tier
/// colors). Localized text (KeyString, tier formats) changes only on Strings.LanguageLoaded, which clears the cache.
/// FTL fuel and demo suffixes are appended by MapNode.DrawName after this method and stay live.
/// </summary>
internal static class MapNodeText
{
    private const string IlSha256 = "BD282B14C712876CF27A72F7126872807668664996BD5ECAD2A11483D4DE82AE";

    internal readonly record struct Key(string Name, Range<int>? Tier, ID<SectorTypeRules>? Sector, ID<FactionRules>? Faction, Rules Rules);

    private sealed record Entry(Key Key, string Text);

    // Weak keys: nodes of an unloaded galaxy are collected with their text.
    private static readonly ConditionalWeakTable<MapNode, Entry> Cache = new();

    static MapNodeText() => Strings.LanguageLoaded += (_, _) => Cache.Clear();

    internal static MethodInfo Target =>
        AccessTools.Method(typeof(CareerGameModeManager), nameof(CareerGameModeManager.GetMapNodeText))
        ?? throw new MissingMethodException(nameof(CareerGameModeManager), nameof(CareerGameModeManager.GetMapNodeText));

    /// <summary>Key inputs above are exactly what this IL reads; any change to it rolls the mod back to vanilla.</summary>
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        Patcher.RequireIl(instructions, IlSha256, "CareerGameModeManager.GetMapNodeText");

    /// <summary>Hit: append the cached text and skip vanilla. Miss: run vanilla, remembering where its text starts.</summary>
    /// <remarks>Last prefix and first postfix, so the cached span holds vanilla's text only, never other mods' hooks.</remarks>
    [HarmonyPriority(Priority.Last)]
    public static bool Prefix(CareerGameModeManager __instance, MapNode node, StringBuilder sb, out (Key Key, int Start) __state)
    {
        CareerGameModeManager.MapNodeData data = __instance.GetMapNodeData(node);
        var key = new Key(node.Name, data.TierRange, data.SectorTypeID, node.ControllingFaction, GameApp.Rules);
        if (TryGet(node, key) is { } text)
        {
            sb.Append(text);
            __state = (key, -1);
            return false;
        }
        __state = (key, sb.Length);
        return true;
    }

    [HarmonyPriority(Priority.First)]
    public static void Postfix(MapNode node, StringBuilder sb, (Key Key, int Start) __state, bool __runOriginal)
    {
        if (__runOriginal && __state.Start >= 0) // vanilla text only, never another mod's replacement
        {
            Store(node, __state.Key, sb.ToString(__state.Start, sb.Length - __state.Start));
        }
    }

    internal static string? TryGet(MapNode node, in Key key) =>
        Cache.TryGetValue(node, out Entry? entry) && entry.Key == key ? entry.Text : null;

    internal static void Store(MapNode node, Key key, string text) => Cache.AddOrUpdate(node, new Entry(key, text));
}
