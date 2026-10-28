using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer.Data;
using Cosmoteer.Factions;
using Cosmoteer.GalaxyMap;
using Cosmoteer.Localization;
using Cosmoteer.Modes.Career.Map;
using Halfling;
using HarmonyLib;
using Xunit;

namespace Performance.Tests;

public class MapNodeTextTests
{
    private static MapNode NewNode() => (MapNode)RuntimeHelpers.GetUninitializedObject(typeof(MapNode));

    private static readonly MapNodeText.Key BaseKey = new("Sol", new Range<int>(2, 3), new ID<SectorTypeRules>(1), new ID<FactionRules>(4),
        (Rules)RuntimeHelpers.GetUninitializedObject(typeof(Rules)));

    [Fact]
    public void TranspilerMatchesRealGameIlUnchanged()
    {
        var original = PatchProcessor.GetOriginalInstructions(MapNodeText.Target);
        var checkedIl = MapNodeText.Transpiler(original).ToList();

        Assert.Equal(original.Select(i => i.ToString()), checkedIl.Select(i => i.ToString()));
    }

    [Fact]
    public void TranspilerRejectsChangedIl()
    {
        var original = PatchProcessor.GetOriginalInstructions(MapNodeText.Target);
        original.Insert(0, new CodeInstruction(OpCodes.Nop));

        Assert.Throws<InvalidOperationException>(() => MapNodeText.Transpiler(original).ToList());
    }

    [Fact]
    public void HitOnlyForSameNodeAndKey()
    {
        MapNode node = NewNode();
        Assert.Null(MapNodeText.TryGet(node, BaseKey));

        MapNodeText.Store(node, BaseKey, "text");
        Assert.Equal("text", MapNodeText.TryGet(node, BaseKey with { Name = new string("Sol".ToCharArray()) })); // by value
        Assert.Null(MapNodeText.TryGet(NewNode(), BaseKey));
    }

    [Fact]
    public void AnyKeyInputChangeMisses()
    {
        MapNode node = NewNode();
        MapNodeText.Store(node, BaseKey, "text");

        MapNodeText.Key[] changed =
        [
            BaseKey with { Name = "Vega" },
            BaseKey with { Tier = new Range<int>(2, 4) },
            BaseKey with { Tier = null },
            BaseKey with { Sector = new ID<SectorTypeRules>(2) },
            BaseKey with { Sector = null },
            BaseKey with { Faction = new ID<FactionRules>(5) },
            BaseKey with { Faction = null },
            BaseKey with { Rules = (Rules)RuntimeHelpers.GetUninitializedObject(typeof(Rules)) },
        ];
        Assert.All(changed, k => Assert.Null(MapNodeText.TryGet(node, k)));

        MapNodeText.Store(node, changed[0], "new");
        Assert.Equal("new", MapNodeText.TryGet(node, changed[0]));
        Assert.Null(MapNodeText.TryGet(node, BaseKey));
    }

    [Fact]
    public void LanguageLoadedClearsCache()
    {
        MapNode node = NewNode();
        MapNodeText.Store(node, BaseKey, "text");

        // Raise the game's own event the way Strings.LoadLanguage does.
        var languageLoaded = (EventHandler<EventArgs>?)typeof(Strings).GetField(nameof(Strings.LanguageLoaded), BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        Assert.NotNull(languageLoaded);
        languageLoaded(null, EventArgs.Empty);

        Assert.Null(MapNodeText.TryGet(node, BaseKey));
    }
}
