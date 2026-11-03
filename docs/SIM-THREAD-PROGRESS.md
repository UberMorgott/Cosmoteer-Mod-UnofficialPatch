# UNOFFICIAL_PATCH_SIM_THREAD: progress (branch perf/sim-thread, worktree E:\DEV\cosmoteer\UnofficialPatch-wt-simthread)

## 2026-11-02 session 2 (user approved the big route)

- Prior decision found: D:\tmp\claude\D--DEV\9ac2c577-a11b-46b1-8be9-60a5e3b36c61\scratchpad\TASKS-decouple.md + m1-spec.md
  = route (a) "runtime whole-method replacement" (view publication M1 S1-S10, then M2 sim thread). That is the
  "replace the core at startup" decision. S1 (protocol, ec6fdf4) and S2 (VIEW_DETECT, 23ece60) are done.
- Codex verdict on a cooperative (mutually excluded) sim thread as first step: BLOCK as proposed, GO for staged view route.
  - Park only at completed ticks (after FixedUpdater.Advance's FixedUpdate event + FixedUpdateCount++,
    FixedUpdater.cs:95), never mid-tick: SP GUI writes the sim directly (BattleHelper.cs:437), wrappers half updated,
    TValue past the interval.
  - MP fence = previous clock target fully processed before the next AdvanceNetworkTime (GameRoot.cs:1611,
    BaseMPManager.cs:908); SP: tick batch before new input (SPManager.cs:80).
  - World ownership boundary = before Director FrameStarting / clock / audio / HandleQueue (Director.cs:335) to after
    frame callbacks. Director.ExecuteOnMainThread (Director.cs:791) switches inline->queued by thread identity.
  - VIEW_DETECT phases are global main-thread state; before any overlap it needs per-thread phases + write checks.
  - Present overlap unproven: ticks create/destroy graphics resources (check HalflingPlatformWDX locking).
  - Ships-first view migration needs culling, refresh callbacks, topology and buffer lifetime isolated, not just
    quad + pose copies.

## Milestone T: SP adaptive tick budget (coordinator priority, before the split)

- src\TickBudget.cs + SpCatchUp.DeltaTime: SP real dt per frame capped so game time <= ticks fitting
  budget = max(0.65*T, T - nonTickFrameWork) at EMA tick cost, floor = x1's game time this frame. Non-tick work =
  Director FrameStarting..FrameEnded minus ticks (Present/vsync excluded). Averages owned per SimRoot.
  Default on in SP, UNOFFICIAL_PATCH_TICK_BUDGET=0 kills. MP untouched (lockstep pacing from host input ticks).
- Codex: 2 BLOCK rounds (x8 slowed when it fits; vsync counted as work; cross-sim EMA) fixed -> GO.
- Bench (fleet-combat, 40 ships, 60 s, user's settings TargetFps=FpsTarget30, 2 reps ABBA, contention ~24% other CPU
  from Track A): results D:\tmp\claude\E--DEV-cosmoteer\9f18e096-53d5-484c-a704-d95076b4c1de\scratchpad\bench-tb

  | arm | fps avg | frame p50 | frame p99 | ticks/s |
  | --- | --- | --- | --- | --- |
  | x1 budget off | 31.4 | 18.5/24.5 | 120/130 | 16.9/15.2 |
  | x1 budget on | 27.5 | 24.3/25.4 | 117/148 | 16.6/14.8 (x1 is never capped: noise) |
  | x8 budget off | 7.7 | 117/111 | 361/365 | 17.8/21.3 |
  | x8 budget on | 17.6 | 55.8/54.2 | 150/147 | 15.5/16.4 |

  - x8: fps x2.3, p99 -59%, sim rate -18% and now equal to x1's. FPS still below x1: at equal tick rate the x8
    frame's non-tick work is ~25 ms vs ~15 ms at x1 (1000 = 16*~35 ms ticks + fps*other), so equal FPS would need
    x8 to tick slower than x1. Only the sim thread removes the per-frame tick (and its spikes).
- Determinism: fleet-combat-determinism at x8, budget off vs on: `bench -compare` identical over 100 samples,
  ticks 30..3000.
- Bench c6e15e6: whitelists UNOFFICIAL_PATCH_TICK_BUDGET / UNOFFICIAL_PATCH_SIM_THREAD env (arms were rejected before).

## Next

- Commit T after bench + hash equal.
- View route ships-first (S4 poses + S5 quads per m1-spec.md), single-threaded first with FrameHash equality,
  then sim worker with tick-boundary ownership handoff and per-thread VIEW_DETECT.
