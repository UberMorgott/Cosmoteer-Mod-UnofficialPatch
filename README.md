# Unofficial Patch for Cosmoteer

Community unofficial patch for vanilla Cosmoteer: fewer garbage-collection
stutters and fixes for vanilla multiplayer desyncs. Ships, parts, balance and
visuals stay vanilla. Full list of fixes: [FIXES.md](FIXES.md).

## Install

1. Install [Yet Another Mod Loader][yaml]: subscribe, close the game, run
   `Installer.bat` in `<Steam>\steamapps\workshop\content\799600\3577650065`.
2. Subscribe to this mod, enable it in the Mods list, trust its DLL when
   asked, restart the game.

## Multiplayer

The mod changes simulation code, so every player needs the same mod version,
loader and game version. If a game update changes the patched code, the mod
switches itself off and the game runs vanilla.

## Experimental: Server GC (opt-in)

Off by default. Create an empty file
`unofficial-patch-server-gc.txt` in the Cosmoteer folder in Saved Games (next to Saved Ships), start the
game once and restart: the mod sets `System.GC.Server: true` in
`<game>\Bin\Cosmoteer.runtimeconfig.json` (only that key; original backed up
once as `Cosmoteer.runtimeconfig.json.unofficialpatch.bak`). Delete the file,
start once and restart to undo. Smoother on average (p99 frame -18%, GC pause
-71%), but more short spikes (worst frame +33%, 0.9 → 3.0 per minute). Without the mod, remove the
key by hand; Steam "verify files" also resets it. Details: [FIXES.md](FIXES.md).

## Build and test

Requires the .NET 10 SDK and a local Cosmoteer install (`GameBin` in
`UnofficialPatch.csproj`).

```powershell
dotnet build
dotnet test
qgate -All -Full
```

[yaml]: https://steamcommunity.com/sharedfiles/filedetails/?id=3577650065
