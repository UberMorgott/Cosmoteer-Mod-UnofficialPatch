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

## Build and test

Requires the .NET 10 SDK and a local Cosmoteer install (`GameBin` in
`Performance.csproj`).

```powershell
dotnet build
dotnet test
qgate -All -Full
```

[yaml]: https://steamcommunity.com/sharedfiles/filedetails/?id=3577650065
