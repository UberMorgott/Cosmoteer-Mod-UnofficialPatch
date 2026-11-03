namespace UnofficialPatch;

/// <summary>
/// Entry point called by Yet Another Mod Loader (public static void GameLoadInitializer()). The loader calls it from its
/// GenericApp.ApplicationMain prefix, before the game loop. GameApp construction and initial graphics setup have
/// already run, so live allocation checks are still required to verify that prior JIT inlining cannot bypass a patch.
/// </summary>
public static class Main
{
    private static bool _initialized;

    public static void GameLoadInitializer()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        Log.Init();
        // false = rolled back to vanilla (logged); nothing else depends on the patches. An incomplete rollback throws on
        // purpose: the loader logs initializer exceptions and still starts the game, which is safe because every patch
        // keeps vanilla sim behavior, so no exit is forced.
        _ = Patcher.Apply();
    }
}
