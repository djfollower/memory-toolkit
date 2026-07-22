# Changelog

## [0.6.0] - 2026-07-22

Driven by a second production codebase (see `docs/INTEGRATION.md`) — this one already had a pool, so
every item here is about the case 0.5.0 could not handle: a project with an incumbent pooling system
and hundreds of call sites that cannot be rewritten in one change.

### Added
- **`MemoryToolkit.Migration.PoolBridge`**: a backing implementation for a project's existing global
  pool API. Brownfield projects reach their pool through a handful of extension methods called from
  hundreds of places, so replacing the pool is not a landable change — the toolkit has to run
  *underneath* the existing API. Re-point those methods at the bridge and every call site keeps
  working on scope-owned pools. `ScopeResolver` makes the per-prefab ownership decision explicit
  (the one the incumbent usually made by accident); `UnknownInstances` is the migration dial for the
  period when two registries are live; `UnknownInstanceCount` / `LazyPoolCount` are the metrics that
  say whether the migration is working.
- **`GameObjectPool.WasWarmedUp`**, surfaced in the Memory Inspector as *(not warmed)*. A pool created
  lazily by a first `Get` took its capacity from whichever call site happened to run first and paid an
  Instantiate during gameplay to exist at all. Previously indistinguishable from a warmed pool.
- **`MemoryScope.TryGetPool`**: answers "is this prefab already pooled, and by whom?" without creating
  a pool as a side effect of asking.
- **`PooledInstance.IsPooled`**: the O(1) "is this in the pool?" query, so an incumbent's equivalent
  (typically a linear scan of the free list, often run on every return) can be re-pointed at it.
- **`GameObjectPool.DoubleReleaseCount`**: repeat releases are harmless but a non-zero count means
  call sites are unsure who owns the release.

### Changed
- **`GameObjectPool.Release` now throws on an instance from another pool**, naming both prefabs.
  Releasing into the wrong pool is the characteristic failure of a migration running two registries
  side by side; it previously failed obscurely inside the pool's internal accounting.
- **Double release is now a documented O(1) guarantee** rather than an unstated behaviour. When the
  contract is unwritten, every call site adds its own guard, those guards are usually a linear scan,
  and they get applied inconsistently — one real codebase had 20 such hand-written guards, each
  duplicating a check the pool already performed internally.
- `PooledInstance.Release` delegates the already-pooled case to the pool instead of short-circuiting,
  so one place owns the idempotency rule and repeat releases are counted wherever they originate.

### Docs
- `docs/INTEGRATION.md`: the brownfield companion to `ADOPTION.md` — how to read an incumbent pool for
  its six usual failure modes, why the churn greps mislead in a project that already pools, and a
  migration order that never runs two registries blind. Also records that all five hazards in
  `ADOPTION.md` §4 reproduced independently in a second, unrelated codebase.

## [0.5.0] - 2026-07-22

Driven by adopting the toolkit in an existing production codebase (see `docs/ADOPTION.md`). Every
item here is a gap that real integration exposed and the README's API tour did not.

### Added
- **Component-typed pooling**: `pool.Get<T>()` and `pool.Release(component)`. Game code is
  component-typed while the pool was GameObject-typed, forcing a `GetComponent` into the exact hot
  path the pool exists to optimize. The lookup is now resolved once per instance and cached on its
  `PooledInstance`. `Get<T>` throws when the prefab has no `T` rather than returning a null that gets
  dereferenced frames later.
- **`PooledRef<T>` and `PooledInstance.Generation`**: a reference that knows whether it still points
  at the same *occupant* of a pooled instance. Pooling breaks the assumption that non-null means
  still-yours — an instance released and re-taken passes every null check while belonging to someone
  else. Capture a `PooledRef` before an `await`, check `TryGet` after. Non-pooled components are
  supported and are alive while simply non-null, so call sites need not know the difference.
- **`PoolSafetyValidator`** (Assets > Memory Toolkit > Validate Pool Safety, plus an API taking a
  `List<Issue>`): static pre-flight checks for "can this prefab survive pooling?" — ParticleSystem
  Stop Action set to Destroy (self-deleting instances, including child systems), `OnDestroy` doing
  cleanup that will silently stop running, rigidbodies with no `IPoolable` to reset physics state,
  missing scripts, and `Awake`/`Start`/`OnEnable` semantics that change under reuse.
- `docs/ADOPTION.md`: how to triage an existing codebase for lifetime boundaries, what order to land
  the toolkit in, and the pooling hazards that only surface in real projects.

### Changed
- **`MemoryScope.Dispose` order is now specified and guaranteed**: strict reverse of registration
  (LIFO) across pools, arenas, and registered disposables alike. Previously pools were always
  disposed before registered disposables regardless of registration order, which meant a
  hand-ordered teardown method could not be safely replaced by a single `Dispose` — the first step of
  adopting scopes. Register in dependency order and the ordering now carries over.
- `GameObjectPool` resolves each instance's `PooledInstance` once at creation instead of calling
  `GetComponent` on every get and release.

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
