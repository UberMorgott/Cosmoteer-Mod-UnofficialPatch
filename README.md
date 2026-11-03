# Unofficial Patch for Cosmoteer

Found a bug caused by this mod? Please [open an issue][issues] here on GitHub,
not in the Steam comments.

Unofficial Patch is an attempt to speed up Cosmoteer and cure its ailments. It
is made mainly to raise performance in single player and multiplayer. Bugs
found along the way are fixed too, hence "unofficial patch". Ships, parts,
balance and visuals stay vanilla. Full technical list: [FIXES.md](FIXES.md).

## Install

Requires [Yet Another Mod Loader][yaml]. In multiplayer everyone needs the same
version of the mod.

- Steam Workshop: subscribe to [Unofficial Patch][workshop].
- Manual: download a [release][releases] and copy the zip contents into
  `%USERPROFILE%\Saved Games\Cosmoteer\<steamid>\Mods\UnofficialPatch`.

## Performance

Whole mod vs vanilla, 40-ship battle, 60-second average: normal speed
18.4 → 28.9 FPS; at x8 speed 7.6 → 21.2 FPS, worst frames 276 → 133 ms.

Fixes below, biggest gain first. Each was measured alone in the same battle on
the test system below. All numbers are from a preliminary run and are
approximate.

- **Blueprint toggles:** every frame, every ship re-checked the toggles of its
  parts, even when nothing had changed. Now this happens only after a change.
  Close view 152 → 552 FPS, far view 31.6 → 44.3 FPS, far view at x8
  21.2 → 23.9 FPS.
- **Fast speed in single player (x2-x8):** when the computer could not keep
  up, the game squeezed up to 8 game steps into each frame and the picture
  nearly froze. Now each frame runs only as many steps as fit, so the game
  runs slower than the chosen speed but stays smooth. Multiplayer is not
  affected. At x8: 7.7 → 17.6 FPS, worst frames 363 → 148 ms.
- **Heat glows:** heat pipes, heat exchangers and radiators were recoloured on
  every tiny heat change, about 19,000 sprites per frame. Now a glow is
  recoloured only when its colour visibly changes (by one colour step).
  Moving fleet 60.5 → 127.3 FPS, worst frames 36.4 → 21.3 ms.
- **Ship drawing:** when one sprite of a ship changed, the whole ship layer was
  sent to the graphics card again (about 7 ms per frame with 40 ships in
  view). Now only the changed parts are sent. Far view 44.8 → 65.0 FPS, far
  view at x8 23.3 → 26.7 FPS.
- **Idle helper threads:** the game's helper threads kept the processor busy
  while waiting for work. Now they sleep and wake up the moment work arrives,
  leaving more processor power for the real work. The game runs a few percent
  faster.
- **Heat effect values:** radiator and heat exchanger effects were re-checked
  every frame, although most had already settled. Now settled values rest
  until something changes. Close view 441 → 542 FPS, worst frames
  15.0 → 6.7 ms.
- **Collisions of fast objects:** after every bullet hit the game re-checked
  all of about 4,900 contacts on one processor core. Now the checks run on all
  cores, and repeat checks cover only the roughly 300 contacts that changed.
  Collision results are identical.
- **Ship outline:** after any destroyed part the ship's outline was rebuilt
  from scratch. Now it is kept when the destroyed part cannot change it, and
  the rebuild itself does less repeated work.
- **Starting and stopping effects:** when a sound, effect, beam or crew member
  started or stopped, the game searched a list of thousands of objects the
  slow way. Now it uses a much faster lookup.
- **Beams:** every beam tested hundreds of its own ship's parts that it can
  never hit. These parts are now skipped. Hits and damage are unchanged.
- **Construction refresh:** every destroyed part triggered a walk through the
  whole ship to refresh construction work, even with nothing queued. Now
  skipped when nothing is queued (about 1 ms per game step in a 40-ship
  battle).
- **Decals on broken ships:** when a ship broke apart, each decal was looked up
  by its name, letter by letter. Now a ready table is used (about 0.5 ms per
  game step in a 40-ship battle).
- **Fewer short stutters:** the game briefly pauses to clean up memory it no
  longer needs. Many places that kept creating throwaway data every frame or
  every step (part toggles, galaxy map labels, muted music layers, thrusters,
  minimap particles, nebula checks, status effects, ship breakups, sounds,
  drawing) now reuse it, so these pauses come less often. Music sounds exactly
  the same.

## Bug fixes

- **Multiplayer desyncs:** player money, station delivery missions and crew
  working on another ship's job could be changed from two places at once, so
  players' games could drift apart. These changes are now applied one by one
  in a fixed order.
- **RGB Max colour blend** picked the smallest colour value instead of the
  largest and produced broken colours. Fixed.
- The mod log writes a short network summary every 10 seconds (freezes, ping
  per player) to help find the cause of multiplayer lag.

## Test system

- CPU: AMD Ryzen 7 9800X3D, 8 cores / 16 threads, 4.7 GHz base, up to 5.2 GHz
  boost
- RAM: 64 GB DDR5 (2 × 32 GB ADATA AX5U6000C3032G), running at 4800 MT/s
- GPU: NVIDIA GeForce RTX 5070 Ti, 16 GB VRAM, driver 617.14
- Storage: Fanxiang S880 4 TB, M.2 NVMe PCIe 4.0 x4, rated up to 7300 MB/s
  read / 6800 MB/s write
- OS: Windows 11 IoT Enterprise LTSC 2024 (24H2), build 26100.8037
- Cosmoteer 0.30.5 (Steam build 26230688), Unofficial Patch 0.2.20

All measurements and development were done on this PC.

## Bug reports

[Open an issue][issues].

## Build and test

Requires the .NET 10 SDK and a local Cosmoteer install (`GameBin` in
`UnofficialPatch.csproj`).

```powershell
dotnet build
dotnet test
qgate -All -Full
```

[issues]: https://github.com/UberMorgott/Cosmoteer-Mod-UnofficialPatch/issues
[releases]: https://github.com/UberMorgott/Cosmoteer-Mod-UnofficialPatch/releases
[workshop]: https://steamcommunity.com/sharedfiles/filedetails/?id=3832853143
[yaml]: https://steamcommunity.com/sharedfiles/filedetails/?id=3577650065
