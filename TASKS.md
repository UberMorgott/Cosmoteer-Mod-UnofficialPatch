# Performance mod: open tasks

Scope (user, 2026-10-28): this is the single "unofficial patch" mod: every fixable game problem goes here
(FPS, GC spikes, freezes, netcode/input delay, GC runtimeconfig). No separate Netcode mod. All MP players have identical mods.
Planned: write System.GC.Server to runtimeconfig (if bench A/B wins) + a self-removing startup hook in the game folder that
reverts the runtimeconfig change and deletes itself when this mod is uninstalled/disabled.

Baseline (MP, fog off, 144 cap, 20 s trace 2026-10-28): 36 GCs (8-39 ms pauses, ~620 ms), 1.8 GB alloc (~90 MB/s).
Top allocators: GetToggleMode delegate 429 MB (patched), DeWintersTC.Shields closures 243 MB, galaxy map labels 201 MB,
NVorbis 154 MB, ThrusterManager dict 138 MB.

- [x] Patch BaseBlueprintPartNetworkPort.UpdateOperational delegate alloc (d5f9b0c, f670567), deployed to Mods\Performance
- [ ] A/B profile after game restart, same save/camera/cap: MB/s, GC pauses, frame time p95/p99
- [ ] Live check: mixed MP lobby (mod on one side only)
- [x] Other GetToggleMode sites (BlueprintPartStatProvider.UpdateOperational 32.9 MB/20 s, BlueprintPartNetworkToggle.UpdateToggleFromRules): same ldftn/newobj shape, folded into PortToggleMode (fa0c213), not deployed
- [x] Music: muted layers seek NVorbis every buffer (~193 MB/20 s); DeferredSeekStream wraps the "music" factory, replays last two seeks on read (bit-identical, Codex-reviewed), not deployed
- [ ] A/B profile of music + toggle patches in game (alloc MB/s, GC pauses)
- [x] ThrusterManager.CalculateMaximumAccelerationAndRampTimeCached: skip throwaway dict for non-cached directions, presize (31685f3, bd1a7a5), not deployed
- [x] CareerGameModeManager.GetMapNodeText: cache base text suffix, invalidate on language/rules reload (ecc3645, dc38e4e), not deployed
- [x] Vanilla desync: PlayerState.AddMoney from parallel buckets (-13 crew trade delivery on home ship, -17 pre-purchase refund) lost updates; PlayerMoney defers to per-sim pending sum, flushed in SimRoot.ExecuteQueued prefix (Index order), not deployed
- [ ] Vanilla race (Codex find, unverified): ResourceTransferJob.DeliverResources:1433 calls Sim.Mode.OnResourceTradeDelivery directly from bucket -13 -> DeliverResourcesObjective.cs:249,253 mission state writes; also AnticipatedMoneyDelta += (UI only, not hashed)
- [x] ParticleEffect._IsOnMiniview boxed Vector2 into IShape (62 MB/20 s): call RotRect.Contains directly (6301d14, Codex-approved); IL pins now culture-invariant (af5998e), not deployed
- [ ] TempHashSet<T> recycle Clear wipes peak-size buckets (~3.6% main + 3.4% workers, SearchForSources): trim-on-recycle NOT shipped (Codex agreed). Microbench: saves ~0.5-2 us/recycle only if peaks never recur; recurring peaks -> 2x slower + 0.1-25 GB garbage. Needs joint (Capacity, Count at recycle) + peak recurrence per pooled instance and real Clear time from a profile first. Order proof holds (.NET 10 HashSet enumerates entries[0.._count), freelist LIFO, Resize copies in order) but copy ctor/ToHashSet (ConstructFrom) clones freelist holes only for small capacity -> copies of sets with holes are capacity-dependent; only SourceInfo/SinkInfo sets audited (membership-only), TempHashSet<int> (20 sites) not
- [ ] Report DeWintersTC.Shields allocations/CPU to its author
- [ ] Solo vs MP trace on the same save to explain flat solo FPS
- [ ] Own mods: Autofleet RangeCache/AsteroidMasks leaks, Belts BeltManager tick on ships with no belts
- [ ] GitHub repo UberMorgott/Performance + push, workshop texts/preview, release
