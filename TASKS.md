# Performance mod: open tasks

Baseline (MP, fog off, 144 cap, 20 s trace 2026-10-28): 36 GCs (8-39 ms pauses, ~620 ms), 1.8 GB alloc (~90 MB/s).
Top allocators: GetToggleMode delegate 429 MB (patched), DeWintersTC.Shields closures 243 MB, galaxy map labels 201 MB,
NVorbis 154 MB, ThrusterManager dict 138 MB.

- [x] Patch BaseBlueprintPartNetworkPort.UpdateOperational delegate alloc (d5f9b0c, f670567), deployed to Mods\Performance
- [ ] A/B profile after game restart, same save/camera/cap: MB/s, GC pauses, frame time p95/p99
- [ ] Live check: mixed MP lobby (mod on one side only)
- [ ] Measure other GetToggleMode sites: BlueprintPartNetworkToggle.cs:96, BlueprintPartStatProvider.cs:94
- [ ] ThrusterManager.CalculateMaximumAccelerationAndRampTimeCached: skip throwaway dict for non-cached directions, presize (Codex plan)
- [ ] CareerGameModeManager.GetMapNodeText: cache base text suffix (Codex plan; invalidate on language/rules reload)
- [ ] Report DeWintersTC.Shields allocations/CPU to its author
- [ ] Solo vs MP trace on the same save to explain flat solo FPS
- [ ] Own mods: Autofleet RangeCache/AsteroidMasks leaks, Belts BeltManager tick on ships with no belts
- [ ] GitHub repo UberMorgott/Performance + push, workshop texts/preview, release
