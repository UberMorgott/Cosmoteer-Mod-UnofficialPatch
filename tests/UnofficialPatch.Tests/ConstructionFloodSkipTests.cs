using System.Linq;
using HarmonyLib;
using Xunit;

namespace UnofficialPatch.Tests;

public sealed class ConstructionFloodSkipTests
{
    [Fact]
    public void KillSwitchIsOnlyZero()
    {
        Assert.False(ConstructionFloodSkip.IsRequested("0"));
        Assert.True(ConstructionFloodSkip.IsRequested(null));
    }

    [Fact]
    public void TranspilerPinsRealGameIl() =>
        Assert.NotEmpty(ConstructionFloodSkip.Transpiler(PatchProcessor.GetOriginalInstructions(ConstructionFloodSkip.Target)).ToList());
}
