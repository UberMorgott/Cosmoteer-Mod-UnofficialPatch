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

## Next
- Beams (EmitBeams 6.7 ms/tick): decide generalize BeamParallel (creative SP only today) vs other.
- DestroyingBody multicast (sim-relevant order: careful), adjacency iterators, 100-ship scenario `out\trackA\fleet-combat-100-x8.json`.
