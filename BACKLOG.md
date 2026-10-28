# Unofficial patch backlog: community-reported issues

Collected 2026-10-28 from Steam discussions (app 799600). Game 0.30.4. "read" = thread body read; "title" = title only.
Patch notes 0.30.3c/d and 0.30.4 not yet checked: exclude anything they already fixed.

| # | Issue | Area | Evidence | Fix idea (unverified) |
|---|---|---|---|---|
| 1 | Random hangs on Linux/Proton since last update; `--cores 1` works around it | perf | read, 52 replies, [thread](https://steamcommunity.com/app/799600/discussions/0/832745759814356553/) | threading race; find root cause |
| 2 | Hang (sound loops, no input) in saves with ~10 owned ships | perf/save | read, 4 replies, [thread](https://steamcommunity.com/app/799600/discussions/4/805723552519125523/) | likely same as #1; profile with such a save |
| 3 | Memory leak on quickload: each reload laggier until restart | perf | read, 2 replies, [thread](https://steamcommunity.com/app/799600/discussions/4/832745943312959862/) | stale game state kept after load; release it |
| 4 | Late-game/co-op lag from crew pathing | perf/MP | older threads + title; prior art [Reduce Crew Lag](https://www.nexusmods.com/cosmoteer/mods/13) | deterministic pathing cache/throttle (all players same mods) |
| 5 | Overclocks lost after Repair All (stuck unfinished armor/heatpipe tile) | gameplay | read, 5 replies, [thread](https://steamcommunity.com/app/799600/discussions/4/592907722016483933/) | fix repaired-state check |
| 6 | Paint partly lost on reload | save/UI | read, 1 reply, [thread](https://steamcommunity.com/app/799600/discussions/0/568174730608273397/) | no repro yet |
| 7 | Crash when replacing a ship with a loaded one twice | crash | title, 5 replies, [thread](https://steamcommunity.com/app/799600/discussions/4/805722145474837785/) | read crash first |
| 8 | Overflow makes very large ships unloadable | save | title, 0 replies | widen int / guard |
| 9 | MP desyncs ("Out of Sync", "Drunk ship") | MP | title; dev blames cross-CPU float | not fixable by mod; diagnostics only |
| 10 | UI: resource desires single digit only; transfer window font; News button vanishes on background change; can't delete ship saves | UI | title, 0-2 replies | small fixes |
| 11 | NPC ships stuck in high electron-cloud sectors | AI | title, 8 replies | investigate |

Own findings (profiling, see TASKS.md): GC spikes from per-frame allocations, MP input delay ignores jitter and slowest route.
