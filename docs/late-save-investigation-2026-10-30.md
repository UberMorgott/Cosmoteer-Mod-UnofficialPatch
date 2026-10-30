# Late-save investigation, 2026-10-30

## Installation and input selection

The installed user mod was still the one-patch `f670567` build (DLL SHA-256 `415cd3c6c1e48660ad63f9a4eaca7535c1b7b6be17d60afc35bf59eebbbc8689b`). It was backed up and replaced locally with version 0.2.0, SHA-256 `107d3d9fe876b4c91b6f47c6f60556bff8fe2c32544514222d92d891dd98a012`; the game confirms 17 patched methods. This was a local installation, not a Workshop/GitHub release.

Saved games were ranked by file size, then inspected for active ships, wrecks and stasis entries. File size alone is not an active-load measure. The largest, `1.savedgame` (36,389,794 bytes), contains 36,491 stasis entries but only 82 active objects, including 62 asteroids, 18 crewless wrecks, one player ship and one NPC. Its x8 medium-view diagnostic still achieved about x7.93 simulation speed. `stuck` is an earlier world and also maintains x8.

Two further large saves provide different workloads:

| Save | Wrecks | Wreck parts | Active asteroids | Active nuggets | Stasis entries | Manual transfer jobs |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `7` | 107 | 2894 | 8 | 3286 | 32166 | 76 |
| `9` | 76 | 4718 | 12 | 2847 | 32634 | 104 |

Bench commit `17a7787` added pinned-save scenarios, whole-world census and actual draw/focus counters. Its initial loader passed verified bytes without a source path to prevent original-save writes. That preserved vanilla world contents but missed path-bound auxiliary mod state; the fidelity correction below is required for the user's modern saves. Original files remain untouched. Ironman and unknown mode headers are rejected.

## Path-bound save-state fidelity

Autofleet stores exact per-save automation snapshots next to its DLL, keyed by lowercase absolute save path and exact UTC write ticks. Its `BindToSave(null)` bypasses these snapshots and uses the copied current working state. Valid snapshots exist for `stuck`, the 2026-10-28 20:13:58 cooperative autosave, `solo`, and the October 29 autosaves. Their enabled assignments differ from the working state's IDs; the initial benchmark restored zero agents. Those initial modern-save runs are therefore incomplete reproductions, not proof that the user's original world is fast. This is a benchmark-loading defect, not evidence that Autofleet causes the reported slowdown.

No exact snapshots exist for the old `1`, `7` or `9` inputs. Their vanilla world and rendering diagnostics remain useful, but they do not establish reproduction of the modern cooperative campaign. Normal career SP and the benchmark both choose player zero; owner-one automation suppression is a separate SP-versus-coop difference.

Faithful fixtures copy the original save and timestamp, matching snapshot wrapper, profiles and settings into local benchmark artifacts. Only the copied wrapper's `SavePath`, `SaveWriteTicksUtc` and hashed filename are rebased to a deterministic input under the isolated game root. State, schemas, unknown fields and profile contents are preserved. Original save/snapshot/working-state/profile hashes are checked. The generic benchmark loader materializes the verified bytes under that explicit isolated root and passes only the owned path to vanilla `LoadGameInit`; no Autofleet-specific restoration is implemented in Bench.

## Diagnostic measurements before the shader fix

Cosmoteer 0.30.5a, uncapped renderer, workstation GC, requested x8. Actual speed is measured physics ticks / wall seconds / 30. These are exploratory single runs, not clean release comparisons. Profiling and competing CPU work perturb the results.

| Scenario | FPS | Actual speed | Frame p99 ms | GC pause ms/s | Allocation MB/s |
| --- | ---: | ---: | ---: | ---: | ---: |
| `7`, saved view, allocation trace | 487.80 | 7.852x | 7.67 | 68.92 | 177.41 |
| `9`, saved view, sampled thread trace | 139.91 | 7.190x | 49.03 | 79.75 | 252.47 |
| `9`, close view, no trace | 258.24 | 7.537x | 36.64 | 21.26 | 55.08 |
| `9`, far view, no trace | 229.34 | 7.836x | 31.44 | 44.99 | 258.66 |

The last two runs use the same copied save, x8, 3600 warmup ticks and 7200 measured ticks; view widths are 40 and 12000. Measured other-process CPU was 54.6% and 21.0%, respectively, so their timing difference cannot establish a camera-only speed effect. All four runs recorded one actual `DrawEnded` per measured frame, zero unfocused/non-renderable frames, and `ShowMap=false`. The far view was also inspected visually. A brief visible 63 FPS in save `7` was not its sustained measured rate.

## Confirmed allocation mechanism

The `7` allocation trace contains 4.17 GB of sampled allocations. The initial portion includes Autofleet stasis indexing: it decodes 865 modified asteroid/junk records even with zero automated agents, finishing after about 10.7 seconds. This is startup work, not evidence of a permanent leak.

After excluding the first 13 seconds, the remaining approximately seven seconds contain 735 MB of allocation samples. `GraphicsManager.RefreshShaderConstants` accounts for 85.46% inclusively; boxed `Vector3`, `Matrix`, `Single`, `Color` and `Vector2` dominate. The actual boxing is in an inlined D3D backend helper, not in the typed shader-constant storage.

Installed `HalflingPlatformWDX.dll`, `Halfling.Graphics.D3D11.D3D11Shader+D3D11BufferConstant.IsDataDirty<T>(in T)`, compares the stored unmanaged value through `Object.Equals(object)`. Its IL explicitly boxes the incoming `T`. Eight non-generic `Update` overloads use this helper. Object equality and typed equality are equivalent for these exact supported types, including NaN, signed zero and the raw integer representation of `RawBool`; bytewise comparison or `==` would not be a safe replacement.

This is a proven source of avoidable render garbage. It does not prove that the user's complete persistent late-world slowdown has been reproduced or that all frame cost comes from this code.

## Shader-only repeated comparison

Eight fresh processes ran baseline/candidate/candidate/baseline for each of saves `7` and `9`, with 3600 warmup ticks and 7200 measured ticks. Baseline DLL SHA-256 is `107d3d9fe876b4c91b6f47c6f60556bff8fe2c32544514222d92d891dd98a012`; the immutable shader-only candidate is `02344e618b978fb35bfd75e6296a7f59e04fef3d7464f5b874269092e29de907`. The later raw-generic compatibility guard changes installation checks, not the rewritten hot path.

Two-run medians:

| Scene / arm | FPS | Actual speed | Frame p99 ms | GC pause ms/s | Allocation MB/s |
| --- | ---: | ---: | ---: | ---: | ---: |
| `7` / baseline | 702.79 | 7.997x | 2.20 | 12.98 | 111.36 |
| `7` / shader only | 688.66 | 7.990x | 2.30 | 5.32 | 15.57 |
| `9` far / baseline | 267.21 | 7.889x | 24.62 | 47.10 | 304.35 |
| `9` far / shader only | 267.78 | 7.756x | 31.37 | 37.59 | 215.49 |

Allocation per frame fell 85.71% and 29.31%; GC pause time fell 59.06% and 20.19%. Average FPS did not materially improve. Long-tail metrics worsened: `9` median frames over 33 ms/min rose from 96.42 to 147.35. The shader-only candidate does **not** satisfy the no-regression publication gate.

All eight runs completed actual draws on every frame, with zero unfocused/non-renderable frames. Other-process mean CPU ranged from 4.7% to 14.8%; strict pre-run quiet requirements failed, so results retain `annotate` contention records. `7` start/end censuses match across all arms. In the last `9` baseline, three crew were in space rather than aboard at measurement start; total crew and final census match. Timing is not an exact deterministic replay.

Peak-commit ranges overlap: `7` baseline 6.67-8.48 GB versus candidate 7.64-8.26 GB; `9` baseline 7.19-8.21 GB versus candidate 7.44-7.70 GB. One first-pair difference does not establish a persistent memory regression. A fresh-process final shader build (`95d1aca9cce517b6c701dc53e01876f4f69a38093c4006f614f157289d797a0d`) also passed actual rendering inspection and allocation profiling; profiled timing is excluded from this table.

## Remaining dominant allocation: event cleanup

The final shader-only `9` trace confirms that shader-value boxing is removed from the dominant allocation stacks. After trimming the first 13 seconds, 1,509,448,952 sampled bytes remain: 84.44% are `Object[]`, with 83.42% in `PartGraphics.UpdateColor -> SceneComponent.remove_BeforeDraw -> MulticastDelegate.DeleteFromInvocationList`.

Dirty part colors subscribe one handler per component. A dirty draw updates its quads and clears `Dirty`, retaining `Registered`; the following clean draw unregisters it. Removing N handlers separately copies descending delegate invocation arrays, producing quadratic garbage. The fix must preserve that delayed cleanup, mixed subscriber order, native snapshot invocation, nested draws, detach/re-add behavior and partial progress when a callback throws. Moving updates into the existing pre-draw queue would change ordering and reentrancy.

`BeforeDrawUnsubscribe` defers only this pinned clean-branch unsubscribe while its `ShipRenderer` is invoking `BeforeDraw`. The original multicast snapshot still runs in its original order. Pending removals are applied before every ordinary add/remove, before fetching a nested snapshot (including its null branch), and in invocation cleanup. Between barriers, single-handler removals commute by delegate equality; reverse filtering removes exactly the last requested occurrences. Survivors are combined once in their original order through the native sparse-event updater. Color writes, flags and ordinary detachment stay vanilla.

State is weakly keyed by renderer and reused; successful or failed dispatch cleanup clears pending handler references. Target-aware hashing prevents thousands of same-method part handlers from degenerating into dictionary hash collisions. Uninterrupted cleanup batches scale linearly; repeatedly interleaved native mutations can still force repeated rebuilding. Direct private-event-storage manipulation or other Harmony hooks specifically on the bypassed clean accessor are outside this compatibility claim.

The regression fixture executes the real publisher/accessor IL and real `PartGraphics` dirty/clean/detachment paths. It covers mixed callbacks, equal duplicate delegates, multicast/null mutations, same-owner and cross-owner nesting, remove-all/empty nested draw/add/new nested draw, exceptions, weak references, and preserved `AfterDraw`. A 2048-handler native cleanup allocates 22,533,344 bytes; batched cleanup allocates about 232,168 bytes, with allocation growth checked at 512 and 2048 handlers. A second large-population case retains persistent subscribers and checks survivor order and linear allocation growth.

## Combined fixes in game

Combined candidate SHA-256: `89548ce4aacd52c70383ea4ae742f42e6f5813f64550289424a397fd7e4899e8`; the game confirms all 29 targets applied. Both repositories pass their final full quality gate without warnings. Bench commit `2d1af30` includes the generic owned-input identity fix; the fixture scripts remain local artifacts.

| Scene / arm | Runs | FPS | Actual speed | Frame p99 ms | GC pause ms/s | Allocation MB/s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `9` far / baseline | 2 | 242.00 | 7.856x | 29.51 | 43.64 | 278.68 |
| `9` far / combined | 2 | 290.26 | 7.921x | 23.27 | 18.11 | 61.45 |
| faithful `stuck` / baseline | 1 | 603.83 | 8.002x | 5.41 | 22.73 | 150.08 |
| faithful `stuck` / combined | 1 | 603.60 | 8.002x | 4.29 | 8.37 | 35.98 |
| faithful cooperative auto / baseline | 1 | 460.46 | 8.001x | 6.79 | 24.15 | 96.82 |
| faithful cooperative auto / combined | 1 | 462.68 | 7.998x | 5.87 | 18.40 | 62.73 |

`9` ran baseline/candidate/candidate/baseline; its start/end censuses match across all four processes. Its combined allocation per frame falls 81.60%, GC pause time 58.50%, and median p99 21.17%. First baseline other-process CPU was 17.1% and is marked contaminated; the reverse baseline was 9.1%, candidates 6.3% and 4.5%. The apparent median FPS gain is therefore not a clean hardware-isolated estimate. All eight runs completed actual draws for every frame, with zero unfocused/non-renderable frames.

The two modern save pairs explicitly bind the copied per-save snapshot and restore one local automated ship each; copied profile bytes and wrapper State are preserved. The baseline and candidate start censuses match. End worlds differ as autonomous commands and background indexing evolve, so these are diagnostic single pairs. Modern-save p99 improves, while their average FPS remains essentially unchanged. The severe sustained below-x1 user symptom still has not been reproduced.

Memory is not an improvement claim: `9` median peak commit rises 7.60 to 8.44 GB, although end private bytes fall 7.00 to 6.81 GB. The modern single pairs also show higher candidate peak/end memory. Their rare worst frames increase (`stuck` 22.61 to 25.58 ms; cooperative auto 30.40 to 39.24 ms). Lower average allocation and GC cost do not establish no regressions on every metric. Publication remains blocked; no live multiplayer performance or desync validation is claimed.

## Selected speed versus performance

The larger `223` diagnostic visibly changes from requested x8 to selected x1 while rendering around 340 FPS. Original settings contain `AutoSlowWhenEnemySpotted=1`. Vanilla `GameGui.UpdateIndicators -> ObjectIndicator.ShowPing -> MPSetClockScaleInput` can issue that one-shot speed change when a new enemy indicator appears; the displayed value changes immediately and the confirmed value follows. This is a separate policy effect, not proof of CPU saturation. It does not automatically restore x8 on zoom-in and does not explain a persistent selected-x8 slowdown by itself. The x1 diagnostic is excluded from the x8 comparison table.

Both `223` processes run at about 29.96-29.97 physics updates/s. Baseline/combined: 336.69/353.12 FPS, p99 9.26/8.14 ms, allocation 136.81/26.25 MB/s, GC pause 23.50/8.82 ms/s. Worst frames are 87.14/115.46 ms; both retain one frame over 50 ms in the 90-second window. This is an additional diagnostic, not proof that requested x8 was maintained.

## Final profile and local installation

The fresh combined trace `combined-profile/results/29de9eb3b486b954.json.nettrace` has about 390 MB of allocation samples after trimming 13 seconds, versus 1.51 GB in the shader-only trace. `Object[]` drops from 84.44% to 12.28% of sampled bytes. Event-handler creation and linear delegate reconstruction still allocate; this patch does not make rendering allocation-free. Actual draws, zero unfocused/non-renderable frames and unchanged start/end fleet counts validate the run; timing under tracing is excluded from the comparison table.

Version 0.2.1 is installed locally with the exact tested combined DLL hash above. Previous 0.2.0 is backed up at `C:\Users\Morgott\Saved Games\Cosmoteer\76561197996210591\ModBackups\Performance-before-0.2.1-20261030`; restore its DLL, mod.rules and strings to roll back. Original modern save/snapshot/working-state/profile hashes remain unchanged. No Workshop/GitHub publication or push was performed.

The continuing performance target is higher FPS on heavy maps. These allocation fixes are a completed step; next diagnosis must measure dominant update/draw/upload/worker-wait time on the current build. A Vulkan port is not justified by the allocation evidence alone.

## Implementation and compatibility

`ShaderConstantBoxing` transpiles only the eight concrete D3D11 buffer `Update` methods. It retains the original buffer address calculation and passes that address and the original input to `EqualityComparer<T>.Default` through an unaligned native read. The existing writes, dirty flags, shader switching and drawing remain in place. Generic game methods are not detoured.

Compatibility guards cover every caller, the helper's raw open IL and generic operands, its normalized closed-int IL, and game/SharpDX equality dispatch. The raw guard matters: importing only the helper closed over `int` would hide a future change from generic `T` to a concrete `int` operand. Existing atomic rollback and module-specific R2R inliner checks also cover the backend assembly. Its current table contains no inliners for these eight methods.

Headless tests execute the real vanilla and rewritten `Update` bodies against native buffers. Coverage includes every individual matrix/vector component, guard bytes, aligned and unaligned offsets, an already-dirty buffer, NaN payloads, infinities, signed zero and noncanonical `RawBool` values. The original paths allocate 24-80 bytes per call; warmed rewritten paths have no per-call allocation in both unchanged-value and changing-value loops. Startup graphics can run before mod initializers, so headless success alone does not prove live interception; a fresh-process allocation trace is required as well.

## Evidence

Local artifacts: `E:\DEV\cosmoteer\Bench\out\stuck-20261030`. Inputs and traces stay local and are not committed.

- `late-7-test/results/56e1bbcd1b2db831.json[.nettrace]`; `allocation-stacks.json`, `allocation-steady.json`.
- `late-9-test/results/1671dc354ef36a6d.json[.nettrace]`; `cpu.speedscope.json`.
- `late-9-close/results/95bab92bceb032f8.json`.
- `late-9-far/results/6b216fe59dce1f37.json`.
- `shader-baseline-mod`: immutable version 0.2.0 snapshot.
- `combined-candidate-mod`, `combined-comparison-summary.json`, `combined-comparison.log`: combined binary and eight-run evidence, including contention and complete censuses.
- `faithful-input-manifest.json`, `original-data-final-check.json`, `installed-0.2.1.json`: owned input identities, original-data checks and installed binary/backup identity.
- `combined-performance-final-qgate.log`, `faithful-bench-final-qgate.log`: full gates without warnings.

The old installed-build comparison is not release evidence: one historical arm (`2b6e28390be741b5`) contains 2891 unfocused frames. Do not generalize the zero-unfocused result above to every earlier experiment.
