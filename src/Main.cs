namespace Performance;

/// <summary>
/// Entry point called by Yet Another Mod Loader (public static void GameLoadInitializer()). The loader calls it from its
/// Application.Main prefix, i.e. before any game code has run.
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
