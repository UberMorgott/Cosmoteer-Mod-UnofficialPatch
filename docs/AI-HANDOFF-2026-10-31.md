# Cosmoteer performance investigation: compressed AI handoff

**PAUSED by the user on 2026-10-31, approximately 11:28 Moscow. Stop optimization, experiments and automatic continuation; await a new user instruction. The FPS objective is NOT achieved. Do not shut down the PC: the original shutdown permission was conditional on verified completion.**

## 1. Scope, locations and current state

Original objective: diagnose zoom/load-dependent FPS and simulation-speed collapse; deliver/install a genuinely effective mod; verify 1/10/40 largest cruisers, close/far, idle/combat, x1/x8. Do not hide slowdown with an FPS cap or slower simulation. Preserve saves and unrelated work. Full 24-case acceptance and live multiplayer validation remain incomplete.

Resolve path aliases recursively; all refer to this local machine:

| Alias | Absolute path |
| --- | --- |
| P | `E:\DEV\cosmoteer\UnofficialPatch` |
| B | `E:\DEV\cosmoteer\Bench\out\stuck-20261030` |
| A | `B\acceptance` |
| E | `E:\DEV\cosmoteer\Bench\out\performance-20261030` |
| T | `D:\tmp` |
| U | `C:\Users\Morgott\Saved Games\Cosmoteer\76561197996210591` |
| O | `U\Saved Games` |
| G | `D:\Steam\steamapps\common\Cosmoteer` |

- Both repositories are on `main`. Unofficial Patch behavior/diagnostic source: **`4307d7d`, version 0.2.16**; owned clone helper: `c259113`. Bench: **`bc5db78`**. Source includes held changes; do not assume its default patches equal the installed build.
- **Actually installed: Unofficial Patch 0.2.1**, DLL SHA256 **`89548CE4AACD52C70383EA4AE742F42E6F5813F64550289424A397FD7E4899E8`**, at `U\Mods\UnofficialPatch`. No later experiment was promoted, published or pushed.
- Main settings enable only loader `D:/Steam/steamapps/workshop/content/799600/3577650065` and `Mods/UnofficialPatch`, as authorized. Other mods were disabled for isolation, not restored. Backup: `U\settings.rules.before-performance-only-20261030-223828.bak`; mod backup: `U\ModBackups\Unofficial Patch-before-0.2.1-20261030`.
- Server-GC marker `U\unofficial-patch-server-gc.txt` is absent; installed runtimeconfig has no explicit `System.GC.Server`. Bench runs explicitly recorded workstation GC unless testing otherwise.
- At pause: no Cosmoteer/bench process, no active WPR recording. Other applications/sessions were preserved.
- Hardware: Ryzen 7 9800X3D, 8C/16T, RTX 5070 Ti, approximately 61.55 GiB RAM. Game 0.30.5a, bundled **.NET 10.0.12**; development/probe host **10.0.14**. Distinguish them.
- Game MVID `fd7c0bba-64d3-40b4-a571-bf8b34eb4277`; HalflingCore MVID `29d04acb-ae49-4f3b-8499-f03c9144a3a9`. Loader 1.4.4.0; loader-supplied Harmony 2.4.2 MVID `dc2e7251-4b84-4883-90eb-eb05a041522c`. Never replace its Harmony DLL.

User clarification: the agent-link requirement was intended for Claude Code. **Codex should use its own subagents for implementation/review, without agent-link.** Communicate in Russian; code/comments/commits/briefs in English.

## 2. Strongest current findings

1. **Camera collapse reproduced independently of combat:** same seeded 40 stock Algorithm cruisers, 207,000 parts, 15,200 crew, no AI/bullets. Close run `754fad73c62fc78f`: **86.89 FPS, actual x7.893, mean Draw callback 0.704 ms**. Far `3639b699c9970362`: **24.33 FPS, x5.340, Draw 16.667 ms**. Both focused/renderable, selected x8, no pause/override. One run per camera; starting ticks differ slightly. Evidence: `A\seeded-idle-40-zoom`.
2. **Real scheduled CPU, not just EventPipe ancestry:** installed 0.2.1 WPR run `235d54d495b675b3`, owned PID24184/main TID32104. Interior `[1000,19000)` ms excludes setup/stop/rundown: 91,558 owned kernel samples, 99.9705% stacked, 1 ms interval, lost0/truncated=false. Main inclusive DoUpdate **57.00%**, DoDraw **40.70%**, MemmoveInternal **19.32%**; 3,370 shown copy samples pass through atlas Draw or crew DrawInterleaved. Main off-CPU only **277.06 ms/18 s**; Ready6.46 ms. Seven workers: 43,626/72,001 inclusive SpinWait samples; outer/inner-loop attribution unresolved. Do not add overlapping inclusive/thread metrics. `T\cosmoteer-native-kernel-cpu-20261031\findings.md` and interior JSON; capture `A\native-cpu-idle-40-far\capture-summary.json`. ETL SHA256 `96AB3E7230104AB25B9A29D297EE9C81779A9994CD990515B2E94DE6CD82696D`.
3. **Atlas writes are genuinely changing, predominantly color-only.** Latest real game `2dea8ddba8a0a200`, `A\atlas-field-census-idle-40-far`: 40 ships/207,000 parts unchanged, x8 selected, ticks304→7509 (7,205), 1,070 fully focused/rendered frames, no overrides. Schema5 ready/status=ok/monotonic at both joined boundaries. Measured 17,883,759 writes: **0 identical; 17,870,912 different; 12,847 unknown; errors0**. Different partitions exactly: **15,543,705 Color-only (86.977682%), 0 Color+other, 2,327,207 Other-only**. Successful intervals206,787: different182,459/unknown24,328/identical0/empty0. This is frequency, **not 87% of CPU, bytes or GPU time**, and not an FPS fix.
4. That census uses opt-in atlas mode3 and its existing synchronization. It executes original Mark/setter/ChangeCount unchanged. No simple identical-write skipping opportunity was observed. Field layout is explicitly checked: AtlasQuad464 B, four AtlasVert116 B at0/116/232/348; Color occupies `[40,56)` in each vertex. Raw bytes preserve signed-zero/NaN payloads.
5. **Global tint is already hoisted:** ShipRenderer multiplies `Graphics.Color` by ship/pass factor; shader `_color` multiplies vertex color (`G\Data\base_atlas.shader:208`). Roof opacity is separately uniform. Per-part `sprite.VertexColor * partColor` remains baked into vertices. Do not implement another common ship-tint hoist.
6. `ManagedShipQuad.Color` already skips equal values, then calls UpdateQuad→MakeQuad, rebuilding geometry/rotation too. Actual dominant per-part providers/rules and whether color can safely update without rebuilding geometry remain **unmeasured/unproven**. Mutable sprite properties, native setter behavior and all dependent fields must be respected.

## 3. Complete experiment-family ledger

Numbers describe their specific fixture, not universal performance. "Native" in later graphics diagnostics often means native backend **with installed Unofficial Patch 0.2.1**, not an entirely unmodded game. Detailed chronology: `P\docs\bench-2026-10-30.md`, `late-save-investigation-2026-10-30.md`, `performance-acceptance-2026-10-30.md`, `TASKS.md`, `FIXES.md`.

| Experiment | Result / disposition | Evidence |
| --- | --- | --- |
| Original allocation bundle: toggle delegates, deferred muted-music seeking, thruster dictionary, map labels, particle boxing | Sources included toggle429 MB/20s, music~193 MB/20s, particles~62 MB/20s. Bundled locally; no separate causal A/B for every fix. | `P\TASKS.md`; Bench `out\reports\stationary-allocation-20261029-131653.json` |
| PlayerMoney/MissionTrade/ForeignJobArrival; telemetry | Source-confirmed parallel mutation hazards; headless checks. Other audited vanilla buckets had no further confirmed race. Live MP still pending. | `P\FIXES.md`, `TASKS.md` and source |
| InputDelay (`fa119fc`, later removed) | Impaired two-instance LAN: zero stalls both arms, delay11.1→18.4 ticks (~230 ms extra); clean2→2.05. Reordering inflated loss. Rejected; >=3-client/bursty proof absent. | `TASKS.md` LAN entries; precise old raw stand path not recovered |
| Original optional Server GC | p99-18%, pauses-71%, >33ms frames-74%; worst+33%, short spikes0.9→3/min, RAM higher. Mixed, optional only. | `FIXES.md`; Bench `out\reports\stationary-allocation-20261029-095110.json` |
| RoofOpacity | 24 Meteor far26.82→26.78 FPS; close single pair57.52→68.71 with unequal CPU. No robust FPS gain. | `E\comparison-summary.json`; baseline was pre-change Unofficial Patch, not vanilla |
| MultiColorMax | Native RGB Max used Min with -infinity seed; actual-component tests fixed it. Vanilla rules inspected do not use RGB Max; no FPS claim. | `E\final-validation.json`, `final-smoke`; `P\docs\bench-2026-10-30.md` |
| Save-loader fidelity and AutoSlow | Initial null source path skipped Autofleet snapshots. Corrected owned-path fixtures restore one local agent. Faithful stuck/coop retain x8;223 selected x1 via enemy auto-slow. Saved-world sustained below-x1 remains unresolved. | `B\faithful-input-manifest.json`, `original-data-final-check.json`, `faithful-stuck`, `faithful-coop-auto`, `combined-coop223` |
| ShaderConstantBoxing only | Saves7/9 allocation/frame-85.71%/-29.31%, GC pauses-59.06%/-20.19%; save9 p9924.62→31.37 ms and more >33ms frames. Mechanism confirmed; shader-only no-regression gate failed. | `B\shader-comparison-summary.json`, `shader-late-7-stable-x8`, `shader-late-9-far-x8` |
| BeforeDrawUnsubscribe + shader, installed0.2.1 | 2,048-handler cleanup22.53→~0.232 MB headless. Save9 bundle242→290.26 FPS, p99-21.17%, allocation/frame-81.60%, pause-58.50%; CPU/memory/worst-frame caveats. Modern-save FPS nearly unchanged. Local install, no publication. | `B\combined-comparison-summary.json`, `combined-profile`, `installed-0.2.1.json` |
| Heavy render/callback/smoothing diagnostics | 24 Meteor far25.01 FPS/x5.63 versus close58.62/x7.95. Empty-toggle shortcut coverage0; no wholly settled smoothing managers. | `B\heavy-phase-far`, `heavy-phase-close`, `heavy-current-cpu`, `single-meteor-rule-census` |
| TempHashSet trimming | Helps only nonrecurring peak sizes; recurring peaks slower/more allocation. Order/copy-capacity semantics complicate replacement. Rejected without workload recurrence evidence. | `P\TASKS.md` |
| Cached surviving multicast delegate | BLOCK: runtime Combine can reuse hidden backing-array spare slots and retain removed targets even when visible invocation lists match. WeakReference alone does not fix republished storage. No implementation. | acceptance notes; native CoreLib storage analysis |
| Callback bulk snapshot copying | Eight arms: far24.06→24.63 FPS, close p9938.24→44.07 ms; four contaminated arms. Excluded. | `B\callback-abba-comparison.json`, `callback-source-experiment` |
| UIToggle specialization0.2.3 | Far+5.9%, update/tick-8.50%; close worst+12.9%, GC/tick+18.38%, commit+9.50%, unequal CPU. Held/uninstalled. | `B\clean-leaf-abba-comparison.json`, `clean-leaf-abba-summary.md`, `leaf-candidate-mod` |
| Largest-ship matrix + deterministic loading | Ranked719 stock designs; Algorithm5,175 parts. Matrix24 cases prepared, incomplete. Bench3708669 seeds Ship.TryLoad before crew construction. Seeded camera result in section2. | `B\stock-combat-ship-ranking.json`; `A\manifest.json`, `scenarios`, `seeded-idle-40-zoom` |
| Static backend0.2.4, atlas mode1 | Dynamic/static29.86/29.54 FPS, p9952.20/55.04 ms, different CPU. Census222.4 MB live/334.7 capacity over360 lists. No gain. | `B\atlas-census\backend-logs`, `atlas-static-candidate-mod` |
| Partial upload0.2.5, mode2 | One-ship smoke858.57 FPS/x8. Initial40-ship feature refused legitimate worker CreateQuad. Worker-safe variant reduced requested bytes40.91% but22.95 versus23.58 FPS, p9970.42 versus66.62. | `A\partial-thread-diagnostic`, `worker-40-far`, `worker-control-40-far`; `B\atlas-worker-candidate-mod` |
| Coalesced uploads, mode3 (`902387...`) | Mode3/mode2:24.12/23.59 FPS; p9971.29/65.55 ms; requested bytes-71.86%,193/965 calls/frame; other CPU18.2/12.0%. Lower work, no clean speedup. | `A\coalesced-40-far`, `coalesced-control-exact-40-far`; `B\atlas-coalesced-candidate-mod` |
| Native CPU / EventPipe / contention | Copy and worker spin confirmed, but sampled Monitor ancestry != blocking. Explicit contention and kernel findings in sections2/4. | `A\native-cpu-mode3`, `native-cpu-combat`, `native-cpu-idle-40-far`; T analysis folders |
| Idle parking0.2.6 | CPU5.431→3.075 cores(-43.38%), FPS+2.43%, p99+4.65%, unequal load. Reverse control had377 unfocused frames/camera drift. Off. | `A\parked-40-far`, `spinning-control-40-far`, `idle-reverse-*` |
| Four-heap Server GC, no DATAS, atlas3+parking | Workstation/server24.213/19.933 FPS; p9964.521/88.677 ms; worst85.7/2428.7 ms; pause24.8/153.6 ms/s. Rejected. | `A\gc4-screen-40-far\reports\largest-40-idle-far-x8-20261031-020933.json` |
| Compound toggle-key comparers | Real fixed-toggle keys only48 collision pairs. Native/strong-hash/exact-equality hit12.581/17.789/12.874 ns; no allocation. Synthetic collision grid misleading; rejected. | `T\cosmoteer-key-hash-probe-20261031\result.json`, `microbenchmark.json` |
| Occupied-cell comparer | Actual7,688 cells/capacity8,419. Additive hit10.590→9.813 ns but misses/small sets worse; mixers slower. Unconditional replacement rejected. | `A\geometry-one`; `T\cosmoteer-cell-lookup-probe-20261031\actual-algorithm.json` |
| Dirty-color handler filtering | Inspection/filter3.26–16.23x slower than native handlers;336 allocation-free windows. Rejected. | `T\cosmoteer-color-noop-probe-20261031\results.json`, `correctness.json`, `summary.md` |
| Crew-search reuse census | Idle873,565 calls/25,021 duplicates(2.86%); combat186,683/9,349(5.01%). No errors/overflow. No cache implemented. | `A\crew-search-census-40-idle-far`, `crew-search-census-40-combat-far`; Benchc1add9a |
| CullCadence0.2.7 (`ddc8852`) | Native low-index revisit1,206 ticks atN1,206/P30. End zero-part backlog249→51(-79.5%); FPS~+3%, p99~+0.7%, damage/load differed and one annotated launch. Cleanup proven, FPS fix unproven. | `A\cull-smoke-one`, `cull-ab-combat-40`, `cull-ab-combat-40-last-native-annotated` |
| Polygon spans / SinCos cache | Polygon estimated main ceiling~0.18–0.55%; SinCos~1.60–2.03% main, high-hit cache flat/slower, changing angles~2x slower. Rejected offline. | `T\cosmoteer-polygon-span-probe-20261031\results.json`; `cosmoteer-sincos-cache-probe-20261031\sincos-results.json` |
| Beam target/precondition census | 49.68M calls,99.6919% repeats, no observed identity/provider change. Guard definition2:33,784,802 calls,99,962/100,097 paired guards pass;135 unknown. Repetition is not purity or saved CPU. | `A\beam-target-combat-40`; `beam-filter-preconditions-combat-40` |
| RaycastScratch0.2.8 (`59d1932`) | Native proof: stale miss payload/retained callback/cross-world nested corruption. Enabled scratch cleanup works; BOTH live reload arms had0 surviving old GameRoot/SimRoot. Same-world tree traversal reentry unsupported. First save attempt refused native blockers. | `T\cosmoteer-raycast-retention-probe-20261031\proof.json`; `A\raycast-retention-duel-drain` |
| Beam memo0.2.9/loader0.2.10 | 0.2.9 refused required loader patches. Exact loader tuple allowance enabled0.2.10, but39.37M calls all native: SecondaryFilter guard rejected defaults;0 fills/hits/comparisons. | `A\beam-memo-verify-smoke`, `beam-memo-verify-combat-40`; `T\beam-memo-loader-patch-proof-20261031.json` |
| Creative secondary0.2.11 (`cb33f0f`) | 35,323,284 native comparisons/0 mismatches. Normal ABBA FPS-8.8%, ticks/s-15.8%, p99+3.8%, allocation/s+17.3%. Compatibility resolution allocated~6.042 MB/s. Rejected; old load-RNG caveat too. | `A\beam-memo-abba-combat-40`, `beam-allocation-profile`; `T\cosmoteer-beam-allocation-analysis-20261031\comparison.json` |
| Beam metadata resolution0.2.12 (`c4a6eb8`) | 30,781,197 native comparisons/0 mismatch/ready. Normal A/B/B stopped before finalA after seed defect found. No acceleration proof; Career/MP pending. | `A\beam-identity-verify-combat-40`, `beam-identity-abba-combat-40\superseded-experiment.json` |
| Hull rebuild census | First bind refused wrong field. Corrected:14,936 removals,5,700 strict candidates,1,688 matched episodes, ordered hull/circle bits identical. Complete point-domain/pool/alias/cache-exit proof incomplete; no preservation patch. | `A\hull-rebuild-combat-40-binding\run\results\92467810fabfe8c7.json`; `T\cosmoteer-hull-complete-point-domain-proof-20261031.md` |
| Dedicated owner Lock0.2.13 (`ee159ea`) | STA/QueueingSyncContext/sent-message parity passes. Strict ABBA FPS+1.8%, speed+3.1%, p99-1.9%, p95+0.5%, allocation/s+2.4%; candidate CPU lower. Small/confounded; off. | `A\atlas-owner-lock-abba-40-far\validated-arms.json` |
| Read-only write census0.2.14 (`934af84`) | 17,971,313 writes;0 identical,12,852 unknown;0 identical upload intervals/errors0. Simple duplicate suppression unsupported. | `A\atlas-write-census-idle-40-far\census-summary.json` |
| Latch-store removal0.2.15 (`74a7133`) | Strict ABBA25.7668→25.685 FPS(-0.32%), speed-0.57%, p99+2.37%, GC pause+2.93%. No gain; stop lock/latch tuning without new evidence. | `A\atlas-latch-abba-40-far\comparison.json`, `validated-arms.json` |
| Owned clone/live-prefix | Helperc259113:13 order/exception/ABI checks + native JIT preparation only; unintegrated. Prefix short-copy blocked on mapped-resource/context lifetime and reentry proof. | `P\src\AtlasDrawClone.cs`; `T\cosmoteer-atlas-prefix-clone-probe-20261031` |
| AVX2 non-temporal copy / tint hoist | D3D11 full-copy66 staging readbacks/240 timing windows; no win at1/4/16MiB (native41.58/156.45/617.87 us versus44.33/157.92/618.42). Global tint already uniform. Both paths rejected. | `T\cosmoteer-gpu-copy-screen-20261031\result.json`, `README.md`; shader/source anchors in section2 |
| Field census0.2.16 (`4307d7d`) + Benchbc5db78 | 82 focused mod cases,35 Bench cases, fresh full gates/Releases without advisories/warnings. Actual17.87M different writes:86.98% Color-only. Diagnostic only, original writes retained. | `A\atlas-field-census-idle-40-far\run\results\2dea8ddba8a0a200.json` and `.atlas.json` |

## 4. Evidence interpretation and implementation boundaries

- EventPipe SampleProfiler counts are stack observations, not scheduled CPU or GPU duration. Draw callback wall time is not GPU execution time. WPR kernel SampledProfile+CSwitch supplies the later CPU evidence.
- Explicit contention capture PID3432:8,877 fully stacked paired waits, lost/unpaired0.8,866 waits have TeamSightCircles ancestry; no observed atlas wait ancestry. Main waits40.817 ms by timestamp versus60.782 ms native Stop payload: different metrics. Sampled atlas Monitor ancestry still substantial. `T\cosmoteer-atlas-write-contention-20261031\findings.md`.
- `dotnet-trace --providers` overrides the CLR profile mask. Old profile-only traces lacked contention0x4000; zero contention events there proves nothing. Verified full mask **`Microsoft-Windows-DotNETRuntime:0x104003C01F:5`** preserves GC/common+Stack+Contention. `T\cosmoteer-idle-render-analysis-20261031\atlas3\contention\stack-contention-provider-proof.json`.
- Native Dynamic upload: Map(WriteDiscard)→capacity CopyBlock→Unmap→dirty=false. Native Draw performs buffered flush/state setup BEFORE upload. Pre-uploading earlier or temporarily changing capacity is not equivalent. Native atlas lists draw their live Count, but shortened-copy lifetime/reentry safety remains unresolved. Do not enable it.
- Window WM_PAINT can reenter `_DoFrame`, but a running native ApplicationMainIterator (state-3) returns false without nested DoDraw. `_DoFrame` can nevertheless invoke FrameEnded/continuations and terminate its loop. This is source/IL evidence, not a tested shipping fix; no window guard was implemented. It does not prove absence of all callbacks during Map/Unmap.
- Never detour shared generic GAME bodies, even closed instantiations. The helper creates an owned nongeneric clone/delegate instead; caller pins/foreign-patch/lifecycle guards are still mandatory. Disable features before all-or-nothing unpatch rollback.
- Full acceptance requires equivalent initial state, camera/focus, selected and confirmed speed, no pauses/auto-slow/overrides, real battle damage/bullets, compatible mods and comparable background load. Old synthetic combat comparisons omitted pre-load ship RNG seeds and are exploratory; do not complete their abandoned final arm.
- Use current driver `E:\DEV\cosmoteer\Bench\tools\bench\bench.exe`. Latest scenarios seed native ship loading before crew creation (`fast-rand-u64-v1`), record actual tick boundaries, and exclude trace/verification/census-requested runs from timing summaries even when activation fails. Frame-ended tick thresholds can overshoot.
- Matrix uses stock `builtin_ships/Monolith/Combat/Algorithm.ship.png`, SHA256 `28F70519E0A3E8393EDB2290A12BD7567F441E5CA3754453F98A6FA401382542`. Counts1/10/40; one-cruiser combat is a duel with one identical opponent (2 actual ships);10/40 split between opposing teams.
- On future authorized work: build/test via `E:\DEV\SessionCoordinator\broker\rb.ps1`; driver owns game/GPU leases. No builds while measured game runs. `qgate -All -Full` must be clean including advisories before game deployment; Release warnings0. Frozen artifacts are immutable. No publish/push without explicit authorization and real no-regression gain. Do not force-close other work.

## 5. ALL recorded used saved-game payloads

Inventory coverage: **179 raw result JSONs** under `E:\DEV\cosmoteer\Bench\out`,34 saved-input runs,15 existing save copies. These identify **nine distinct used payloads: eight available, one missing**. All six original user saves were freshly SHA-compared to pinned inputs and still match.38 other entries in the44-save size inventory have no benchmark-load evidence. `solo`, October29 autos, `drone`, `1111`, `Mult*`, `222` etc must not be mislabeled as tested save inputs.

The archive includes one copy of each available payload, using the archive basename shown below. Binary saves are not embedded in this text. Original files were not edited.

| Archive `saves/` entry | Original / pinned available source | Bytes | SHA256 |
| --- | --- | ---: | --- |
| `stuck.savedgame` | `O\stuck.savedgame` → `B\input\stuck.savedgame` | 2884671 | `715E5F85FC605F9633928FF325322DEED66AD8AB9F41D7CAD2B3EF86D1155F9E` |
| `1.savedgame` | `O\1.savedgame` → `B\input\late-largest.savedgame` | 36389794 | `08E02543C1260A50C2A2A75A1B5F2433DDFFE5058CA8AE73EB1EC32EF2497BFB` |
| `7.savedgame` | `O\7.savedgame` → `B\input\late-7.savedgame` | 35370441 | `CEAA1EFAF6FE4CC0F53E08782393D4E2A3D349BDD93003E606859718C643B244` |
| `9.savedgame` | `O\9.savedgame` → `B\input\late-9.savedgame` | 35727589 | `F70D674A138FFFFC5002B065F2F1A2FD4FCB4EF168108D3DA4C1D2A806564EE0` |
| `223.savedgame` | `O\223.savedgame` → `B\input\large-coop-223.savedgame` | 12120467 | `7E6746B970B8D8A9C34DDF60377A965A3C7FED71D796846A4EE87FD2BF3C74B6` |
| `Auto-Save 2026-10-28_20-13-58.savedgame` | `O\Auto Saves\Auto-Save 2026-10-28_20-13-58.savedgame` → `B\input\late-coop-auto.savedgame` | 6694023 | `FF4070C0D1370A67D833265D2B54132BDD10107E488D413BD76A767F845BAAF4` |
| `Quick-Save 2026-10-31_05-45-41.savedgame` | generated native arm; `Q\Quick-Save 2026-10-31_05-45-41.savedgame` | 342382 | `A27E558DFB37ADB7578C75793F4EBF37D2FCE72243C0718F06E56D6D80713786` |
| `Quick-Save 2026-10-31_05-46-15.savedgame` | generated scratch arm; `Q\Quick-Save 2026-10-31_05-46-15.savedgame` | 344761 | `7E5A93E27BDDD9B175976E382BF5E529E677D96597775854C30E1C15C93487A6` |

`Q = A\raycast-retention-duel-drain\run\roots\mods-perf\save\Saved Games\Quick Saves`.

**Missing ninth payload:** `Quick-Save 2026-10-28_23-30-45.savedgame`, generated by `quickload-leak`, run `ddc5bfed163696cf`. Eight Meteor/30,192 parts, unpatched SP with earlier mixed user mods; one quicksave reloaded four times, five cycles,0 alive old GameRoot/SimRoot, heap~1.278–1.283 GB. Bytes/hash are unavailable, so it is NOT in the archive. Exact filename/load proof: Bench `out\results\ddc5bfed163696cf.json.console.txt` (save23:30:47; loads23:30:54/23:31:30/23:32:05/23:32:38); report `out\reports\quickload-leak-20261028-233314.json`; scenario `Bench\scenarios\quickload-leak.json`. Expected old owned path: Bench `out\roots\mods-base\save\Saved Games\Quick Saves\<filename>`. Do not regenerate it and pretend it is the original.

Saved-input run mapping (all `ok`; IDs locate raw JSONs):

- **stuck:** `f3e57e5b72794da8 bdcd23111702c981 4e975cef0e168d78 2b6e28390be741b5 c7ece82ed215bc0a 1aeb9640593bda66` initial incomplete Autofleet restoration; `aaa2450c1adebaee 7e74b39d81008ea6` faithful.
- **1:** `92c8effef3e1ee46 88b2f37db098bdc1 7b79ada4157bc3f1`. Largest file mostly stasis:36,491 stasis entries versus82 active objects; medium x8~x7.93. Size does not measure active load.
- **7:** `ce61ae389f8fc189 6fc6dd586346f671 50036b6fdb0d3fc6 14b52a6a67e6c6d4 56e1bbcd1b2db831`.
- **9:** `d0d95eac3aa5bdac 6e0a852bf5484195 5cd0a24866843385 4d3568dd8adf3ec7 29de9eb3b486b954 a07d847b3753cb93 6f2903927334ae09 2b8eba72e7bfe84b 1cb43076d249ab28 5ed47576f7174f36 1671dc354ef36a6d 95bab92bceb032f8 6b216fe59dce1f37`.
- **223:** `739a4776ba31b539 6d4a7656aa26a1d2`; cooperative world loaded as SP player0, not actual MP.
- **cooperative auto:** `5b18279aeeac5245` initial incomplete loader; `b89e11cfb2bc9c2c 71d4028c2f7d4a03` faithful owned-path snapshot restoration; still SP0, not MP.
- **generated retention native/scratch:** `920c1fb1deb82b9f` / `5645f2fcc962fe13`. Saved after native save blockers were drained, weapons suppressed for quiet-tail, paused reload with zero reload ticks. Both old-world survivor counts0; states are not a clean equivalent-state performance A/B.

Additional duplicate-copy locations: `B\faithful-stuck\input.savedgame`, `B\faithful-coop-auto\input.savedgame`; corresponding `work\roots\mods-perf\save\Saved Games\Bench Inputs\<SHA>.savedgame`; save9 in `combined-nine` and `combined-profile` owned roots;223 in `combined-coop223` owned root. They have the same payload hashes above.

**Autofleet fidelity is path AND exact UTC-write-ticks bound.** Matching source wrappers: `U\Mods\Autofleet\Autofleet.saves\073BACD246B1F87E4F1D.json` (stuck), `8CBF0B7ABB67F906B957.json` (coop auto). Rebased fixtures: `B\faithful-stuck\user-template\Mods\Autofleet\Autofleet.saves\43F6270D4F7704232097.json` and analogous `faithful-coop-auto\...\58C6F3109DCA45D72DEA.json`, with copied `Autofleet.profiles.json`. Preserve full fixture templates when reproducing. Archive metadata contains raw manifests/wrappers/profiles; **ZIP timestamps alone do not preserve required Int64 ticks**. Never round-trip these ticks through JavaScript doubles. Initial null-path runs are not faithful modern campaign reproductions. Old1/7/9 have no matching historical snapshot. All originals/current working profiles were preserved.

## 6. Binaries, artifacts and resume guidance

Frozen key DLLs (SHA256):

| Folder under B | DLL / SHA256 |
| --- | --- |
| `installed-0.2.1-cpu-profile-mod` | Unofficial Patch `89548CE4AACD52C70383EA4AE742F42E6F5813F64550289424A397FD7E4899E8` |
| `beam-identity-candidate-mod` (0.2.12) | Unofficial Patch `3A69C2FF4D21DA70678292BF6AEB02BF5E4E583B78AE41D6EEB7A636E13E0982` |
| `atlas-owner-lock-candidate-mod` (0.2.13) | Unofficial Patch `C528775F99B5B7576C210D36F679B1076136D0988CEF480458A6522FD5EF9E47` |
| `atlas-write-census-candidate-mod` (0.2.14) | Unofficial Patch `1B7DCDC5061B95CFE678FE246302479C8288BA76B10DB28DE0B639A09657EA05` |
| `atlas-latch-candidate-mod` (0.2.15) | Unofficial Patch `6B0C6B04CAAE8DC48D4CA22DD57A9A7D88D720A80961F9BCB4B102BFEB51659D` |
| `atlas-field-census-candidate-mod` (0.2.16) | Unofficial Patch `237EAB7D52C3F50641F0D043B7A26B3E1F5021235FF4626E0211E78D9C21FE2D` |
| `atlas-field-census-bench-mod` | Bench `9E3E0EF2DD746A8FD22ED9D963FFE05245CE632D444D29C3729DA906357F2EB5` |

Latest clean gates: `T\performance-atlas-field-fullgate-20261031.log`, `performance-atlas-field-release-20261031.log`; `bench-atlas-field-final-fullgate-20261031.log`, `bench-atlas-field-final-release-20261031.log`. Focused logs: `atlas-field-census-{red,green}-20261031.log`, `bench-atlas-field-{red,green}-20261031.log`. Source reviews were independent; no field-census FPS improvement was claimed.

Archive layout: this one English handoff, eight available save payloads, faithful-save metadata and a compact selection of existing JSON/scenario evidence. Large ETL/nettrace/cache trees, original game DLLs and arbitrary user configs are not bundled; exact local evidence paths above remain authoritative. Never overwrite frozen artifacts or originals when unpacking.

If the user later resumes: first verify current git/process/settings/installed hashes; do not restart stopped work automatically or blindly replay old comparisons. The latest actionable observation is color-only writes87%, not an implemented optimization. Possible follow-up: identify actual provider/rule and weighted upload impact; investigate a narrowly guarded color-update/geometry reuse or data-layout improvement. All mutable sprite fields, native equality/ChangeCount/dirty/event behavior, rendering parity and full acceptance still require proof. Avoid more speculative lock/latch/SIMD tuning already rejected. No Vulkan/Rust engine rewrite was attempted or justified.
