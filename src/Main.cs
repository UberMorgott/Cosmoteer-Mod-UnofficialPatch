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
        Patcher.Apply();
    }
}
