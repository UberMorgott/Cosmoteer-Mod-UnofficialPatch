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

## Phase 3
- FastParallel instrumented (diag `PERFORMANCE_FP_STATS=1`, src/FastParallelStats.cs: vanilla EnableProfiling, per top-level region wall / summed batch wall / main share / longest batch / first worker join, logged every 600 frames). 100 ships x8 (`b9`): every per-ship region 90-99% busy over 8 threads, worker join <2 us, longest batch 0.13-0.68 ms vs region wall 0.6-1.8 ms. No imbalance, chunking or spin problem; phase-2 "48% waiting" was truncated worker stacks in the sampled profile. Per frame (~1.1 ticks): regions 12.7 ms wall, 97 ms summed batch time. Top: FireWeaponsAsyncStep 14.3 ms, Jobs 13.5, Resources 10.7, ShipCrew 10.3, ConvertResources 8.4, SetThrusterActivations 6.3, AimWeapons 5.9, Statuses 5.2 (summed per call). Codex: busy != compute-bound (waits inside bodies count); next = worker CPU stacks per body.
- Worker priority Normal vs vanilla Lowest (b9, 2 reps interleaved): ticks/s +1.1%, frame p50 +3.5% (noise) -> dropped. LPT ordering dropped (no imbalance to fix).
- FireWeaponsAsyncStep samples (s100): ~60% in RayCastFirst (broad-phase wrapper 1020, polygon raycast 516 of 2524).
- ThreadCount 15 vs 7 (8C/16T, b10, interleaved off,on,on,off): parallel ms/tick 12.2 -> 10.6 (-13%), tick p50 28.8 -> 27.3 ms (-5%), sim ms/tick -1.6%, frame p50 +6% (both reps). Spinning workers on the main thread's SMT sibling slow the serial part; not shipped. Det-x8 7 vs 15: identical over 3000 ticks. b13 (base,pk,pk+t15 x2 interleaved): sim ms/tick 34.3 / 34.5 / 33.2 (pk+t15 -3%), parallel ms/tick 12.0 / 13.0 / 9.7 (-19%); kept as knob only, not default.
- detci3 (10 arms, `PERFORMANCE_BEAM_PARALLEL=1`, det-x8): all identical to ref (`out\trackA\detci3\reports\detci-det-x8-20261102-171703.json`). main fast-forwarded to 689a00e, qgate -All -Full green, not pushed.
- Commits: cdf58ad diag (FP stats, `PERFORMANCE_FP_THREADS`), c9f805a beams (below).
- Beams (opt-in BeamParallel, Creative SP), Codex APPROVE for opt-in (c9f805a): coverage 40 ships x8 5-7 -> 46-64 records/tick (query 1.3-2.1 ms/tick off main), 100 ships 26-30/tick (~1 ms/tick); VERIFY 100 ships 35531 records bit-for-bit, 0 mismatches (221 shield, 315 penetrating, 10100 secondary); det-x8 bp on vs off identical (state hash every 30 + effect hash every tick, 3000 ticks). Remaining stoppers per tick: target graph (unexplained sub-check, needs reason strings), ResourceConverter.OnOperationalToggleChanged / PartMultiToggle toggle listeners on stop, end-media attachment. Default-on (career/MP) not started: Codex requires Career/MP mode audit (AreEnemies/CanDamage/GetPlayerState) + VERIFY/detci there.
- Changes (phase 3): StopEmission is no longer a round barrier when admitted (value listeners as at line 832, no ToggleChanged subscriber, with end media: no attachment + vetted spawn/target graph + safe intensity/ramp-up values); exact TurretWeapon chains and providers vetted (BeamTargetMemo.TurretGraph, BeamParallel only; memo stays FixedWeapon-only). Attenuation stays a barrier (Codex BLOCK: native tail AttenuationLimit/death effects unaudited, Attenuates outcome returned before input comparison).

## Phase 4
- Det loose end closed (Codex agrees): det-x8 `partsEnd` is a pure function of `endedSimTick` (frame overshoot past 3000), not of beam parallel. 27 results (detci2 bp off, detci3 bp on, b8, b12): end 3000 -> 115215, 3001 -> 115217, 3002 -> 115215, 3003 -> 115211, 3004 -> 114950, 3005 -> 114941, 3006 -> 114934; sim hashes compared only at exact ticks 30..3000, all equal.
- Beam parallel modes: Owned/PureBefore accept exact audited sim/game mode pairs (Creative, Career, BuildBattle, Domination, Elimination, Arena) in SP and MP; the SPManager check is gone (memo stays SP). Audit (Codex-checked): every mode override reachable from the worker path (CanDamage, CanAttack, AreEnemies incl. Career truce/protection/wanted, IsInvulnerable, IsHumanPlayerAllegiance) reads only synced sim state; GetPlayerState adds stay routed to native by PlayerKnown/ExistingPlayer; all pinned in NativeDependencies (IL sha A0E218BC...).
- PureBefore returns constant per-reason strings (`target graph: <reason>`), aggregated in the stats log.
- Evidence (mod-v11): det-x8 bp=1 vs off: 100/100 sim hashes + effect-hash file identical (`out\trackA\b15det`). Career late-9 save x8, 2700 ticks, off / bp / bp+VERIFY: identical hashes + effect hash, but 0 records/tick (`target graph: turret aim` 874, `special sight` 314): career combat is untested (`b15career`).
- MP lockstep stand (Bench MpRunner `--mp-scenario out\trackA\mp-fleet.json`, script scratchpad `mp-stand.ps1`): two LAN instances, AI fleets (players 2/3, 40 ships) spawned at sim tick 150 on both, sim hash every 30 ticks. mp2 both bp=1: 52/52 samples equal (ticks 150-1680), 100.2 accepted records/tick on each peer, 0 verify mismatches. mp3 host bp=1 / join off: 49/49 equal, host 87 accepted/tick. Parts 138680 -> ~126k (real combat). mp1 was void for bp (the stand's permanent Harmony patch on NetworkMessenger tripped the foreign-patch guard): the stand now patches only around InitializeNetworking.
- A/B fleet-combat x8 interleaved off,on,on,off (`b15ab`, phase timing): tick ms mean 26.78 / 25.39 / 25.50 / 25.16, serial ms/tick 19.24 / 19.04 / 19.20 / 18.85, process CPU ms/tick 300.8 / 295.2 / 302.3 / 301.2, frame p99 134 / 125 / 126 / 128. No measurable gain -> default-on NOT flipped (Codex gate: needs measured gain). Stays `PERFORMANCE_BEAM_PARALLEL=1`.
- Stoppers per 2845 ticks (det-x8, bp): predict stop `target graph: sight cache stale` 1615, `bounding circle dirty` 222, StopEmission `end media attachment` 180+44, StopEmission target graph sight 176, ResourceConverter toggle 149+50, PartMultiToggle 35+16, turret aim part 25.
- Sight-cache pre-warm rejected (Codex BLOCK): TeamSightCircles.Refresh scans all ships and mode callbacks; SensorManager.ClearCaches changes radii at once but invalidates the team cache via EnqueueDeterministic, so an early refresh can persist stale circles after an invalidation. Needs an equivalence proof or a private (non-native) sight computation.
- Toggle listeners on stop (OperationalPartComponent/OperationalChainablePartComponent.OnOperationalToggleChanged -> IsOperational setter cascade; PartMultiToggle -> its own ToggleChanged cascade): not admitted; deferring them changes native order, admitting needs an audit of the IsOperational cascade. Not started.

## Phase 5 (2026-11-03, serial main thread)
- Fresh WPR (`out\trackA\t5prof`, mod-v12 = main 6ac1882, parking on), main FixedUpdate 8310 samples: buckets 5354 (EmitBeams 1825, FastParallel main share 1804, StructureContiguity 676, SmoothedValue 258), World.Step 1673 (PhysicsToiScan.ClassifyAll 807, of it ~550 kernel/wait: parked-worker wakes on 47-69 passes/step), ExecuteQueued 1215 (flat). Exclusive top: BroadPhaseWrapper 823, PolygonShape.RayCastWithRadius 741, ToI Classify 237.
- Exact DynamicTree.RayCast replica (pooled int stack, no ThreadLocal): no gain in a 1450-fixture microbench (scratchpad `tick5\rcbench`) -> dropped.
- Diag (`t5diag`, temporary BeamRayStats): beam raycast time in the emitter's OWN body tree: secondary 86%, structural 83%, operational 52% (77% overall).
- 84cc3f2 HullRemovalSkip KnownSafe memo (InSafeDomain O(parts) rescan per removal). det-x8 on vs HULL_REMOVAL_SKIP=0 identical; gain not separately measurable (~1% of FixedUpdate by profile).
- 63e4342 BeamOwnBodySkip `PERFORMANCE_BEAM_OWN_SKIP` (default on): per armed beam ray, real native predicates on the own-body census classes the ray filter reaches; skip own walk when all reject. Ion beams have FriendlyShip*Collisions=true (default), so the operational ray accepts own Cat1|Cat2|Cat4 parts and still walks; structural + secondary (SecondaryFilter rejects own ship) skip. det-x8 on vs off identical (402k walks skipped, `t5det3`); A/B 4+4 interleaved (`t5ab2`): serial 15.48 vs 16.12 ms/tick (-0.64, all on < all off), tick mean 22.87 vs 23.27, GC/alloc unchanged. Codex LGTM (2 rounds). Bench a9af58b whitelists the env key; bench mod `out\trackA\bench-mod5`.
- Remaining (serial ~15.5 ms/tick): operational own walk (genuine accepts, not skippable exactly); FastParallel main share (parallel phase); ToI ClassifyAll (incremental classification needs an audit of Fixture.OnCollision/OnSeparation callbacks run inside contact.Update during TOI; not order-changing); ship split (DecalsManager.CopyDecalsFrom, ShipMetadata copy+cull: ~44 MB/20 s allocations); SharedSoundEffect.VirtualSoundInstance.StopImpl quadratic delegate removal (~23 MB/20 s, audio only).
- Runner: scratchpad `tick5\batch.ps1 -JobsFile <file> -Log <log> [-Capture 25,0]` (holds Bench\.game-lock per batch).

## Phase 6 (2026-11-03, serial main thread)
- 90c69dc flaky `ParallelIdleParkingTests.NativeSchedulerIsolatedRegression`: root cause timing, not shared state: under CPU load the main thread drained all 32 outer batches before a parked worker woke (`workerPublished > 0`, 7/72 with 12 parallel children). Main batch now waits (bounded 10 s) for a worker batch; 0/120.
- 9f21e44 TOI incremental classification `PERFORMANCE_TOI_INCREMENTAL` (default on; diag `PERFORMANCE_TOI_VERIFY=1`): after pass 0 only contacts of bodies advanced/restored/solved by the pass, of bodies whose classification inputs changed bit-for-bit (snapshot: velocity, angular, xf, sweep.C, Awake, IsBullet, CCD threshold, IgnoreCCD, BodyType), or new contacts are reclassified; Evaluate -> Cached with the computed toi; MaxSubSteps change -> full. Callback audit (contact.Update in TOI): no Begin/End/Pre/PostSolve or OnSeparation subscribers in Cosmoteer; OnCollision (bullet hits, ArcShield) changes bodies only. det-x8 on / off / verify identical (100 hashes + effect hash), verify 0 mismatches (2880 steps, 70 passes/step, 308 of 4884 contacts classified/pass). A/B x8 4+4 (`t6ab`): serial 16.65 vs 17.20 ms/tick, tick 23.40 vs 25.29, process CPU 103.5 vs 115.9 ms/tick. Codex GO.
- bc34f07 SoundStopHub `PERFORMANCE_SOUND_STOP_HUB`: the four `_actualInstance.Stopped` +=/-= sites go to one OrderedEvent per SharedSoundEffect (same order, snapshot raise); OrderedEvent now flattens multicast handlers on add and removes the last contiguous run on remove (Codex BLOCK fixed).
- ed8d872 SplitAlloc `PERFORMANCE_SPLIT_ALLOC`: pooled RemoveAll in CullPartMetadata and PartStateDictionary.RemoveAll (same predicate/Remove order). Copy pre-sizing skipped: the big resizes (toggle 14 MB, color 5.5 MB per 42 s) are inside PartStateDictionary, which the BlueprintToggleUpdates IL scan forbids touching. det-x8 on vs off identical; A/B x8 4+4 (`t6ab3`, both item-3 switches): 2.02 vs 2.11 MB/tick, gen1 39.8 vs 41.7 per 1000 ticks, serial 15.93 vs 16.31.
- detci 10 arms (det-x8, mod-v18 = all phase 5+6 on): all identical to ref (`out\trackA\t6detci\reports\detci-det-x8-20261103-051309.json`). mod-v18 predates the OrderedEvent composite fix (single-target path unchanged; no composite subscriber in game code).
- 100 ships x8 interleaved (`t6ab100`, 2+2, off = TOI_INCREMENTAL, SPLIT_ALLOC, SOUND_STOP_HUB, BEAM_OWN_SKIP, HULL_REMOVAL_SKIP = 0): tick 32.18 vs 33.54 ms, serial 21.06 vs 21.25, process CPU 166.5 vs 176.4 ms/tick, ticks/s 13.20 vs 12.48, alloc 2.52 vs 2.96 MB/tick, gen0 53 vs 63 and gen1 42 vs 45 per 1000 ticks, frame p99 192 vs 188.
- Bench f196402, 8f010a4 whitelist the new env keys; bench-mod7 built from a clean Bench worktree (Track B had uncommitted FrameHash edits). Analysis script: scratchpad `tick6\an.ps1 -Work <dir> [-Det]`.
- Remaining (serial ~15.9 ms/tick at 40 ships x8): EmitBeams operational own walk; FastParallel main share; split copy resizes inside PartStateDictionary (needs a vetted writer entry in the toggle scan) and RoleManager.CopyRolesFrom 6.5 MB; contiguity iterators (`get_AdjacentSets`, `GetAdjacentCellsWithAdjacency`) ~13 MB/42 s.
