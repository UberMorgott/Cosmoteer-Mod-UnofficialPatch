# What this mod fixes

Player-facing list of every fix and change, used for the Workshop page and change notes.
One line per fix: what was wrong → what the mod does. Newest first inside each section.
Mark entries not yet released as (unreleased).

## Performance: stutter and FPS spikes
- Particle effects checked against the minimap view created a small garbage object on every check (~60 MB per 20 s in big battles). The check now runs without it; which particles are emitted is unchanged. (unreleased)
- Ship parts with network ports, toggles and stat providers created a new object every frame, which triggered frequent memory clean-ups (garbage collection) that cause stutter. Now created once per part. (unreleased)
- Muted background music layers were decoded on every audio buffer even though nobody hears them, producing ~190 MB of garbage per 20 s. Seeking is now deferred until the layer is actually read; audio output is bit-identical. (unreleased)
- Ship thruster acceleration calculation built and threw away a temporary dictionary on every cache miss. It now skips the throwaway and pre-sizes the kept one; results are bit-identical. (unreleased)
- Galaxy map node labels were rebuilt as new strings every frame. The base label text is now cached per node and refreshed on language or rules change. (unreleased)
