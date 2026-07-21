# Changelog

## [0.4.0] - 2026-07-21

### Added
- **Memory Inspector window** (Window > Analysis > Memory Toolkit Inspector), replacing the Pool Stats window: heap overview, frame-scratch usage bar with peak, and per-scope pools/arenas/pinned assets/owned disposables with Trim and Dispose actions.
- **Addressables integration** (`MemoryToolkit.Addressables`, compiled automatically when `com.unity.addressables` is present): `scope.LoadAssetAsync<T>(key)` and `scope.Track(handle)` release Addressables handles with the owning scope.
- **`MemoryScope.Pin(asset)`**: holds a strong reference so `Resources.UnloadUnusedAssets`/`CollectFull` cannot reclaim the asset while the scope lives — the mechanism for keeping configs loaded in a momentary scene alive permanently.
- **Permanent Configs sample**: login scene loads a config, pins it to Permanent, and transitions; the scene's scope dies while the config survives the sweep.

### Removed
- Pool Stats window (superseded by the Memory Inspector).

## [0.3.0] - 2026-07-21

### Added
- Five new scenario samples: Scene Scope Level (level-owned memory with warm-up installer), Match Scope (round lifetimes with owned native data), Frame Scratch Query (zero-alloc per-frame physics scan), Zero-Alloc HUD (change-gated StringBuilderCache label), Low Memory Response (game-side cache shedding on `LowMemoryTrimmed`).

### Fixed
- `GameObjectPool.Dispose` now destroys active (checked-out) instances as well as pooled ones, so disposing a scope mid-gameplay no longer orphans live objects.

## [0.2.0] - 2026-07-21

### Added
- `MemoryScope`: lifetime layers for memory. Scopes own pools, arenas (`CreateAllocator`), and arbitrary `IDisposable`s (`Register`); disposing a scope frees everything at once. Pool lookup falls back to the parent chain.
- `MemoryManager.Permanent` (session scope), `CreateScope(name)` (manual lifetimes), and `CreateSceneScope(scene)` (auto-disposed on scene unload).
- Pool Stats window groups pools by scope; low-memory trim now covers every live scope.
- Scope lifecycle tests; Pooled Spawner sample gained a scene-scope toggle.

### Changed
- `MemoryManager.GetPool`/`Warmup` are now shorthand for the Permanent scope (behavior unchanged).
- `MemoryManager.PoolStat` gained a `ScopeName` field.

## [0.1.0] - 2026-07-21

### Added
- `GameObjectPool` with warm-up, low-memory trim, `IPoolable` callbacks, and double-release protection via `PooledInstance`.
- `MemoryManager` facade: shared per-prefab pool registry, `Application.lowMemory` handling, loading-screen `CollectFull()`.
- `FrameAllocator`: per-frame linear allocator over a persistent native block.
- `StringBuilderCache`: thread-local zero-alloc string assembly.
- Pool Stats editor window (Window > Analysis > Memory Toolkit Pool Stats).
- EditMode test suite and Pooled Spawner sample.
