# Track A: sim tick speed (branch perf/tick-speed, worktree Performance-wt-tick)

Goal: 40-100 ship battle, x8 >= 60 FPS, no spikes. Stay inside the tick (Track B owns Draw / render decoupling).

## Baseline profile (2026-11-02, main f73b7bc, fleet-combat x8, WPR CPU 1 ms, 20 s stacked window)
- Bench run `E:\DEV\cosmoteer\Bench\out\trackA\prof1` (contaminated: other CPU 18%): 7.9 FPS, 20.9 ticks/s (target 240), frame p50 110 ms, alloc 49 MB/s, GC pause 56 ms/s.
- Main thread samples 19.7k: Update 16.8k (FixedUpdate 14.8k), Draw 2.8k.
- Folded stacks: `inspect2.json.folded.txt` there; query script `q.ps1` (scratchpad) groups by caller/child/path.

## Hot list (main thread, samples = ms over ~417 ticks)
1. World.SolveTOI 3511 (8.4 ms/tick), 80% exclusive: CCD contact rescan every TOI pass (every ship is a CCD bullet).
2. WeaponManager.EmitBeams 2796 (6.7 ms/tick): beam raycasts (BeamParallel/BeamFrozen exist, opt-in SP).
3. BucketList.Remove -> List.IndexOf + virtual Equals 1845 (4.4 ms/tick): scene component deactivate / ReAddUpdate.
4. FastParallel main share 1629; StructureContiguity split 1170; ExecuteQueued hit effects (MultiHitEffectRules 1462, hull/bounding 592).
5. ContactManager.Collide 292, Island Solve 289, SmoothedValue 368, PartTimer 229.

## Allocation profile (ETW AllocationTick, same ETL, ~1 GB / 40 s)
- Object[] 182 MB: MulticastDelegate.RemoveImpl (ScenePhysicsWrapper.DestroyingBody per collider 58 MB, Camera.ZoomChanged per PartContinuousEffects 52 MB, VirtualSoundInstance.StopImpl 26 MB).
- GetAdjacentCellsWithAdjacency iterators 54 MB (ConstructionManager.RefreshUnderlyingPartRemoves), IterateAllResults 42 MB (resource networks), music decode float[] 37 MB, ShipMetadata.CopyPartMetadataFrom dictionary resizes ~40 MB on ship split.

## Commits (branch perf/tick-speed), all with kill switches, unit tests bit-identical vs vanilla
- 43e163c SceneBucketRemove `PERFORMANCE_SCENE_BUCKET_REMOVE`
- 151696f PhysicsToiScan `PERFORMANCE_TOI_SCAN` (+ stats log commit)
- 12ee77f BoundingPoints dense grid (under `PERFORMANCE_BOUNDING_POINTS`)
- d545ff9 ZoomSubscribers `PERFORMANCE_ZOOM_SUBSCRIBERS`
- b3f1325 ConstructionFloodSkip `PERFORMANCE_CONSTRUCTION_FLOOD_SKIP`
- Bench repo: aac6826, af3872f whitelist new env keys (driver refuses unknown env in result check).
- Codex: approved SBR + TOI after 2 fixes (nested world scratch, Equals guard at add); later batch review pending.

## Bench batch 3 (queued, 5 launches): `out\trackA\jobs3.txt`, log `run3.log`
- det-x8 (fleet-combat-determinism at x8, effect hash) perf vs all-new-off; A/B fleet-combat x8 perf / off / BEAM_PARALLEL=1.
- Runner: scratchpad `run-locked.ps1 -Out <log> -JobsFile <file>` waits for `Bench\.game-lock`.

## Results batch 3 (mod-v3 = all five, fleet-combat x8, 1 rep each, other CPU 12-19%)
| arm | FPS | ticks/s | frame p50 | p95 | p99 | spikes/min |
|---|---|---|---|---|---|---|
| all new off | 8.18 | 22.68 | 106.8 | 246.3 | 290.9 | 52.0 |
| all new on | 8.99 (+9.9%) | 26.05 (+14.9%) | 100.1 | 204.0 | 256.5 | 30.0 |
| on + BEAM_PARALLEL=1 | 9.00 | 25.32 | 102.5 | 200.8 | 253.1 | 24.6 |
- Determinism det-x8 (3000 ticks, hash every 30 + effect hash every tick): on vs off identical.
- BeamParallel: no measurable gain -> stays opt-in (Codex agrees: audit is creative SP only).
- TOI log: 50 passes/step, 3716 contacts/pass at 40 ships (vanilla = 186k contact checks per step).

## Final A/B (mod-v5 = a3dd5ab, 1 rep per arm, `out\trackA\run5.log`)
| scenario | arm | FPS | ticks/s | frame p50 | p99 |
|---|---|---|---|---|---|
| fleet-combat x8 | off | 7.90 | 20.67 | 116.0 | 308.8 |
| fleet-combat x8 | on | 8.32 (+5.3%) | 22.57 (+9.2%) | 107.1 | 296.9 |
| fleet-combat x1 | off | 41.05 | 18.10 | 14.8 | 106.9 |
| fleet-combat x1 | on | 43.22 (+5.3%) | 18.35 | 13.7 | 114.2 |
- x1 runs 18 ticks/s of 30: the 40-ship battle is below real time even at x1 (tick ~25-30 ms main thread).
- Profile after (prof2, mod-v4): SolveTOI 3511 -> 987 samples (-72%), hull Fill+IsEmpty 1121 -> 613.
- Codex: APPROVE f73b7bc..a3dd5ab.

## Next
- Beams (EmitBeams 6.7 ms/tick): decide generalize BeamParallel (creative SP only today) vs other.
- DestroyingBody multicast (sim-relevant order: careful), adjacency iterators, 100-ship scenario `out\trackA\fleet-combat-100-x8.json`.

## Merge
- detci 10 arms (ref, aa1, aa2, cores1, cores2, noavx512, noavx2, nohwi, tc0, pgo0) on det-x8 with mod-v5: all identical to ref (report out\trackA\detci2\reports\detci-det-x8-20261102-125155.json).
- Rebased on main b816c4d (Track B tick budget), qgate -All -Full green, 556 tests pass, main fast-forwarded to 4bda2fe. Not pushed.
- Remaining gap: x8 40 ships ~8-9 FPS / ~23 ticks/s vs 240 target; top remaining main-thread items: EmitBeams (~24% of FixedUpdate), hit effects (chain lightning, damage -> part removal), ship split (StructureContiguity), FastParallel main share, delegate-removal allocations (DestroyingBody).

## Phase 2
- Beams: BeamParallel=1 covers only 5.7-7.3 beams/tick (0.4-0.6 ms of 6.7 ms moved off-thread); fallbacks are prism chains / target graphs / StopEmission. Codex: waves prototype GO opt-in only, default-on MP NO-GO without full mutation/barrier + career audit. Not shipped.
- 2f08a60 DestroyingBodySubscribers (OrderedEvent): det-x8 on/off identical (3000 ticks, effect hash); interleaved A/B (off,on,on,off) alloc MB/s -6.5%, GC pause -2%, FPS/ticks/s within noise (-2%, TickBudget-capped ~17 ticks/s at x8 now). Codex APPROVE.
- 100 ships x8 (mod-v5, early combat): 4.9 FPS, 9.6 ticks/s, p50 152 ms, p99 1147 ms; main FixedUpdate 48% inside FastParallel regions (per-ship parallel buckets), Draw ~30% of main.
- Hit effects / ship split parallel compute: not started (same MP audit scope as beams).
