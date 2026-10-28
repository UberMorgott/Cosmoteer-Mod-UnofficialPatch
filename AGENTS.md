# Performance mod (unofficial patch): repo rules

Workspace rules: `E:\DEV\cosmoteer\AGENTS.md`.

- Scope: the single unofficial patch for Cosmoteer. Every fixable game problem goes here (FPS, GC stutter, freezes, multiplayer/netcode, desyncs, community-reported bugs). All MP players run identical mods.
- Every commit that fixes or changes game behavior also adds or updates its line in `FIXES.md` (player-facing English, "what was wrong → what the mod does", marked `(unreleased)` until shipped). It is the source for the Workshop page and change notes; on release, drop the `(unreleased)` marks.
- Release gate (user, 2026-10-28): publish to Workshop/GitHub only after bench A/B (`E:\DEV\cosmoteer\Bench`) shows a measurable real gain (fewer GC pauses / lower frame p99 / fewer spikes) with no regressions.
- Open work: `TASKS.md`. Community-reported issues: `BACKLOG.md`.
