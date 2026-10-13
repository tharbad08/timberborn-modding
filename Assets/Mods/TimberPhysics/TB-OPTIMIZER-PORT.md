# TimberPhysics 1.1 performance port (TB Optimizer)

This change moves two **TimberPhysics-only** improvements from [TB Optimizer](https://github.com/tharbad08/TimberbornMods/tree/TB-Optimizer/BenchmarkAndOptimizerV11Port) into the owning mod. It does **not** include TB Optimizer's other Harmony patches, global tick throttling, benchmark, telemetry, or settings UI.

## Source map

| Source | Change |
| --- | --- |
| `Scripts/TimberPhysics.Core/PhysicsSimulator.cs` | Preserve the 0.02-second fixed-step physics loop and maximum four substeps, but stop after completed `Physics.Simulate` calls use ~50 ms total wall time. Remaining accumulated substeps are **discarded**. Exposes `BeforePhysicsUpdate` for terrain flush. |
| `Scripts/TimberPhysics.Terrain/TerrainColliderService.cs` | 32×32 chunks; conservative equal floor/ceiling merging; deduplicated terrain dirty chunks rebuilt once before the next physics update; replacement geometry built before the previous set is disabled/destroyed; full per-cell fallback for this map if merging fails. |
| `Scripts/TimberPhysics.Terrain/TimberPhysics.Terrain.asmdef` | Add a reference to `TimberPhysics.Core` to subscribe to the pre-physics event. |

## Tradeoffs and compatibility

- **Not bit-for-bit physics equivalent:** the catch-up cap intentionally drops elapsed simulated time under overload (the existing four-substep cap did this already). The new wall-clock threshold is checked **after** each native PhysX call and cannot interrupt one expensive call.
- **Collider invalidation is deferred:** terrain edit notifications mark a chunk dirty. A rebuild occurs on `PhysicsSimulator.UpdateSingleton`, before simulation, rather than on every edit notification. Raycasts made between the terrain event and the next physics update can temporarily query the previous collider geometry. If that is unacceptable to the owning author, add an explicit early flush at collision-query boundaries or retain immediate updates for those cases.
- **Vanilla means unmerged per-column geometry in the TimberPhysics mod**, not the base game's terrain collision pipeline.
- **Fail-closed behavior:** BuildChunk cleans up partial creations; rebuild exceptions trigger a full per-column rebuild, switching off merging until the map unloads. If even that rebuild fails, a hard error propagates rather than silently making terrain non-solid.
- Collider destruction is deferred by Unity, so old colliders are disabled synchronously before calling `Destroy`. Replacement colliders are fully built before disabling the previous collection.
- Use this implementation **instead of** TB Optimizer's `PhysicsCatchup` and `PhysicsCollider` patches; activating both would result in two owners for the same code path and risks duplicate rebuilding or patches overriding native behavior.
- Changing the update cadence of global `TickableBucketService` or soil/planting services is deliberately **out of scope**.

## Historical failures we must not repeat

The experimental TB Optimizer terrain merger previously left mixed merged/vanilla dictionaries and duplicate keys after failures, including directional dynamite. Older reflection-based builds also made incorrect assumptions about by-ref/interface terrain column members. A native implementation can read the existing `ColumnTerrainMap.GetColumn` directly, without Harmony reflection; its full rebuild path has explicit ownership of every generated collider.

## Validation status

Source edited and committed; **not compiled in Unity or exercised in Timberborn 1.1 in this environment**. Do not treat this as a verified release.

Suggested test pass in an actual Timberborn 1.1 Unity/game environment:

1. Compile all TimberPhysics assembly definitions, and load a map with the unmodified remaining mod components.
2. Confirm physics stepping/collisions and large-colony frame rate on identical save states; log **both FPS and actual game speed**.
3. Test single terrain edits, bulk edits, repeated terrain changes in the same 32×32 chunk, directional dynamite, tunnels, steep/overlapping terrain, and map edges.
4. Confirm post-edit raycasts before/after the next physics update. If transient stale geometry is undesirable, revise the deferral contract before accepting this PR.
5. Trigger an intentional merged-chunk construction exception and verify a complete per-column restore without duplicate/missing colliders.
6. Save/load, switch maps, toggle terrain colliders in developer tools, and test alongside any other mod that edits the terrain/physics lifecycle.
7. Compare the fork without TB Optimizer's two TimberPhysics Harmony patches, then test with TB Optimizer explicitly configured to disable both.

Source references: [retained optimizations](https://github.com/tharbad08/TimberbornMods/blob/TB-Optimizer/BenchmarkAndOptimizerV11Port/OPTIMIZATIONS.md), [failed experiments](https://github.com/tharbad08/TimberbornMods/blob/TB-Optimizer/BenchmarkAndOptimizerV11Port/FAILED_EXPERIMENTS.md).
