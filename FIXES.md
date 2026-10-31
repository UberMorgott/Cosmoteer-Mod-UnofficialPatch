# What this mod fixes

Player-facing list of every fix and change, used for the Workshop page and change notes.
One line per fix: what was wrong → what the mod does. Newest first inside each section.
Mark entries not yet released as (unreleased).

## Multiplayer: desyncs

- Crew working a job of another ship (moving resources between your own ships, helping build another ship) could change that job from two threads at once in multiplayer, so its progress or crew list could differ between clients. When such a crew arrives or gives up on a path while on its own ship, the step is now queued and applied right after the parallel ship update, the same way the game already handles crew on other ships. All players need this mod. (unreleased)
- Station delivery missions could go out of sync in multiplayer: when two ships traded with the same station at the same moment, the game updated the station's offered delivery mission progress from two threads at once, so an update could be lost or applied in a different order on some clients. These updates are now queued and applied right after the parallel ship update, in the game's fixed ship order. All players need this mod. (unreleased)

- Player money could go out of sync in multiplayer: when two ships of the same player traded with stations (or got pre-purchased resources refunded) at the same moment, the game updated the balance from two threads at once and one change could be lost on some clients. Money changes made during the parallel ship update are now collected safely and applied once, in a fixed order, right after it. All players need this mod. (unreleased)

## Multiplayer: diagnostics

- The mod log gets a 10-second network summary (freezes, input arrival margin, ping per player) to help diagnose multiplayer lag. Log only, gameplay is unchanged. (unreleased)

## Performance: stutter and FPS spikes

- Blueprint network ports repeatedly went through indirect toggle evaluation every frame. Ordinary UI toggles now use a direct path that still reads live state; other rules and incompatible patches retain the original evaluation. (unreleased)
- Large groups of part color handlers unsubscribed one by one after a clean draw, repeatedly copying the same event list. Cleanup is now batched between event mutations, preserving callback order, nested draws, color updates and detach behavior. (unreleased)
- Shader constant updates boxed numbers, vectors, colors and matrices while checking whether their values had changed, creating substantial garbage when drawing ships and UI. The comparison now uses the same value equality without allocating; buffer writes and rendering stay unchanged. (unreleased)
- Every visible ship searched for the ship under the cursor while drawing its roof, even when the hide-roof key was released. The key is now checked first, avoiding repeated spatial searches and temporary garbage; roof appearance and held-key behavior are unchanged. (unreleased)
- Particle effects checked against the minimap view created a small garbage object on every check (~60 MB per 20 s in big battles). The check now runs without it; which particles are emitted is unchanged. (unreleased)
- Ship parts with network ports, toggles and stat providers created a new object every frame, which triggered frequent memory clean-ups (garbage collection) that cause stutter. Now created once per part. (unreleased)
- Muted background music layers were decoded on every audio buffer even though nobody hears them, producing ~190 MB of garbage per 20 s. Seeking is now deferred until the layer is actually read; audio output is bit-identical. (unreleased)
- Ship thruster acceleration calculation built and threw away a temporary dictionary on every cache miss. It now skips the throwaway and pre-sizes the kept one; results are bit-identical. (unreleased)
- Galaxy map node labels were rebuilt as new strings every frame. The base label text is now cached per node and refreshed on language or rules change. (unreleased)

## Graphics: incorrect colors

- MultiColor components using RGB Max combined their inputs with a minimum operation, producing negative-infinite RGB values for finite source colors. They now take the maximum of each channel; other blend modes and alpha are unchanged. (unreleased)

## Experimental (off unless you turn it on)

- Secondary beam raycasts repeatedly validated the same weapon target. A benchmark-only experiment reuses a target within a guarded native query, including supported native secondary filters in Creative mode, and falls back to native calls when state or callbacks are unsupported. Required loader patches are checked explicitly; unverified patches keep the experiment off. Compatibility checks reuse resolved method metadata while checking current hooks, avoiding repeated reflection garbage. Disabled by default; correctness and game performance validation are incomplete. (unreleased)
- Raycast helpers retained their last callback and hit data, could return old payload after a miss, and overwrote temporary results during nested queries. An opt-in experiment clears scratch state on success or failure and preserves active outer helper state. Same-world reentrancy remains unsupported because native traversal stacks are shared. Game memory and performance validation are incomplete. (unreleased)
- Empty wrecks were checked increasingly rarely in large battles. An experimental benchmark-only switch keeps cleanup checks on a fixed cycle while preserving rebuild plans and other native protections. Off by default; game performance validation is incomplete. (unreleased)
- Idle-worker experiment: the game busy-spins while pool threads have no tasks. An opt-in guarded path waits for a work notification after a short spin, preserving native task execution, completion waits and worker priorities. Disabled by default while frame-time and simulation-speed validation are incomplete. (unreleased)
- Sparse atlas upload experiment: tracks native sprite edits and tests exact changed regions or one merged span, with full uploads for structural or unverified changes. Native worker edits and complete draw calls are synchronized per sprite manager. Additional opt-in experiments compare dedicated .NET owner locks and collect read-only byte-identical write/upload-interval counts, separating changes to vertex colors from other bytes when the exact layout is supported; the census never skips a native write or dirty mark. These experiments remain disabled by default pending performance and game validation. (unreleased)
- Atlas synchronization experiment: avoids repeating a shared synchronization write for every sprite mutation while retaining startup publication and all owner locking, including after disable. Disabled by default; performance validation is incomplete. (unreleased)
- Atlas-buffer upload experiment: evaluates the game's alternate buffer update path for ship sprites, retaining native geometry and drawing. Disabled by default; benchmark validation is incomplete. (unreleased)
- Server GC: the game uses .NET's workstation garbage collector. Opt-in switch to the server collector (`System.GC.Server` in `Cosmoteer\Bin\Cosmoteer.runtimeconfig.json`, only that key is changed, original backed up once as `.unofficialpatch.bak`). Vanilla bench: frame p99 -18%, GC pause time -71%, frames over 33 ms -74%, but the worst single frame +33% and short spikes (over 2x median) 0.9 → 3.0 per minute, and more RAM. Enable: create an empty file `unofficial-patch-server-gc.txt` in the Cosmoteer folder in Saved Games (next to Saved Ships), start the game once, restart it. Disable: delete that file, start the game once (the mod restores the previous value if the config was not changed since), restart. If you remove the mod first, delete `"System.GC.Server": true` from that config by hand. Steam "Verify integrity of game files" also resets it. (unreleased)
