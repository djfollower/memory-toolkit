# Memory Toolkit

Exemplary memory-handling utilities for Unity, built around one goal: **allocate up front, recycle forever, never churn the heap during gameplay**. Heap fragmentation in Unity comes from repeated allocate/free cycles of mixed-size objects (Instantiate/Destroy, temporary lists and strings, per-frame arrays). Every API in this package targets one of those sources.

## Lifetime tiers

Memory is organized into three lifetime layers (`MemoryScope`), because grouping allocations by lifetime — release everything that dies together, together — is the structural fix for fragmentation:

| Tier | API | Released |
|---|---|---|
| **Permanent** | `MemoryManager.Permanent` (the static `GetPool`/`Warmup` helpers are sugar for it) | At shutdown |
| **Scene** | `MemoryManager.CreateSceneScope()` — or `CreateScope("Match")` for manual lifetimes | Automatically on scene unload (or manual `Dispose`) |
| **Frame** | `MemoryManager.FrameScratch` | Reset every frame, O(1) |

A scope owns pools, arenas (`scope.CreateAllocator`), and any `IDisposable` (`scope.Register`); disposing it frees everything at once. Pool lookup falls back to the parent chain, so a prefab pooled permanently is never duplicated per scene. Deliberately three tiers, not N — extra tiers create ownership ambiguity that causes more leaks than it prevents. `FrameScratch` stays global rather than per-scope so slices can't outlive a scope.

```csharp
// Level load:
MemoryScope scope = MemoryManager.CreateSceneScope();
scope.Warmup(enemyPrefab, 32);
var levelArena = scope.CreateAllocator(512 * 1024);
scope.Register(new NativeParallelHashMap<int, int>(1024, Allocator.Persistent));
// Scene unloads → pools, arena, and hash map are all freed in one step.
```

## What's inside

| API | Replaces | Fragmentation source removed |
|---|---|---|
| `MemoryScope` (Permanent / Scene / Frame tiers) | ad-hoc lifetimes | Long-session accumulation; whole layers released at once |
| `Migration.PoolBridge` | an existing global/static pool registry | Scene-owned registries that get wiped mid-session; stale asset-derived pool keys |
| `MemoryManager.GetPool(prefab)` / `GameObjectPool` | `Instantiate` / `Destroy` | Prefab instance churn |
| `MemoryManager.FrameScratch` (`FrameAllocator`) | per-frame `new T[]` / `Allocator.Temp` in hot loops | Transient buffer churn; arena is one contiguous block reset in O(1) each frame |
| `StringBuilderCache` | string concatenation, `new StringBuilder()` | Per-frame string garbage |
| `UnityEngine.Pool.ListPool<T>` / `System.Buffers.ArrayPool<T>` (used throughout, use them too) | `new List<T>()` / `new T[]` | Collection churn |
| `MemoryManager.OnLowMemory` (auto-wired to `Application.lowMemory`) | OS killing the app | Sheds pool capacity, unloads unused assets, collects |
| `MemoryManager.CollectFull()` | mid-gameplay `GC.Collect()` | Confines blocking collection to loading screens |

## Quick start

```csharp
// During a loading screen: pre-allocate so gameplay never instantiates.
MemoryManager.Warmup(projectilePrefab, count: 64);

// Gameplay: get/release instead of Instantiate/Destroy.
var pool = MemoryManager.GetPool(projectilePrefab);
GameObject shot = pool.Get(muzzle.position, muzzle.rotation);
// ... later, from anywhere:
shot.GetComponent<PooledInstance>().Release();

// Transient native scratch, valid for this frame only:
NativeArray<Vector3> points = MemoryManager.FrameScratch.Allocate<Vector3>(256);

// Zero-alloc UI text:
var sb = StringBuilderCache.Acquire();
sb.Append("Score: ").Append(score);
label.SetText(StringBuilderCache.GetStringAndRelease(sb));
```

Implement `IPoolable` on pooled components to reset per-use state (`OnTakenFromPool` / `OnReturnedToPool`).

### Pooling component-typed code

Game code holds components, not GameObjects. `Get<T>()` resolves the component once per instance and
caches it, so the hot path never pays `GetComponent`:

```csharp
Projectile shot = pool.Get<Projectile>(muzzle.position, muzzle.rotation);
pool.Release(shot); // component overload — no .gameObject at the call site
```

### Surviving reuse: `PooledRef<T>`

Pooling breaks the rule that a non-null reference is still yours. An instance released and re-taken
is alive, non-null, and someone else's — so a null check passes and you corrupt another system's
object. Capture a `PooledRef` across any suspension point:

```csharp
PooledRef<Enemy> enemy = PooledRef.To(pool.Get<Enemy>());
await LoadLoadoutAsync();
if (enemy.TryGet(out Enemy e)) e.Equip(loadout); // false if it was recycled during the await
```

### Already have a pool? Run the toolkit underneath it

A project that already pools reaches its pool through a few extension methods called from hundreds of
places, so "replace the pool" is not a landable change. `PoolBridge` is a backing implementation for
that existing API — re-point the extension methods and every call site keeps working, now on
scope-owned pools:

```csharp
using MemoryToolkit.Migration;

// The project's own extension methods become one-line delegations:
public static GameObject GetFromPool(this GameObject prefab) => PoolBridge.Get(prefab);
public static void ReturnToPool(this GameObject instance)    => PoolBridge.Return(instance);

// The decision the incumbent usually made by accident, now explicit:
PoolBridge.ScopeResolver = prefab => BattlePrefabs.Contains(prefab) ? BattleScope : MemoryManager.Permanent;
```

The registry stops being owned by a scene object, an instance's owning pool travels on the instance
rather than in a lookup table, and release is always reparented and O(1) double-release safe.
`PoolBridge.UnknownInstanceCount` tracks instances arriving from a still-live second registry — the
number to watch to zero during migration. See [`docs/INTEGRATION.md`](docs/INTEGRATION.md).

### Before you pool a prefab: validate it

**Assets > Memory Toolkit > Validate Pool Safety** runs the static pre-flight checks — a
ParticleSystem whose Stop Action is *Destroy* (it deletes its own GameObject and the pool then serves
fake-null), an `OnDestroy` doing cleanup that silently stops running under pooling, rigidbodies with
no `IPoolable` to reset physics state, missing scripts. These are the failures that look like
anything except a pooling bug. A clean report means "nothing statically disqualifying", not "proven
correct" — see the checklist in [`docs/ADOPTION.md`](docs/ADOPTION.md).

## Addressables

With `com.unity.addressables` installed, the `MemoryToolkit.Addressables` assembly compiles automatically (version define, no hard dependency) and adds scope extensions. Addressables memory is reference-counted — a bundle stays resident until every handle is released — so handles follow the same rule as everything else: **a scope owns them**.

```csharp
using MemoryToolkit.AddressableAssets;

// Level content: handle released when the scene scope dies, bundle refcount drops.
var enemyHandle = sceneScope.LoadAssetAsync<GameObject>("Enemy_Orc");

// Already have a handle? Give a scope ownership of it:
sceneScope.Track(existingHandle);
```

## Permanent configs loaded in a momentary scene

A login/bootstrap scene often loads configs that must outlive it. The rule: **ownership follows lifetime, not the scene that did the loading.** Pin the asset to the Permanent scope; the login scene's own scope stays momentary:

```csharp
// In the login scene:
GameConfigs.Initialize(MemoryManager.Permanent.Pin(balanceConfig)); // permanent
var loginScope = MemoryManager.CreateSceneScope();                   // momentary UI pools etc.

// Later: login scene unloads, CollectFull() sweeps behind the transition —
// the pinned config is still referenced by Permanent, so its memory survives.
```

`Pin` holds a strong reference so `Resources.UnloadUnusedAssets` can't reclaim the asset while the scope lives; disposing the scope drops the reference and the next sweep reclaims it. With Addressables, use `MemoryManager.Permanent.LoadAssetAsync<T>(key)` instead — the Permanent scope then owns the load handle. See the **Permanent Configs** sample.

## Diagnostics

**Window > Analysis > Memory Toolkit Inspector** is the live view of everything the toolkit is handling, organized by scope: heap overview (managed used/heap, Unity allocated/reserved), the frame-scratch arena with a usage bar and high-water mark, and per scope its pools (active/pooled/total), arenas, pinned assets (with `GetRuntimeMemorySize`), and owned disposables — plus per-scope Trim/Dispose buttons and toolbar actions for simulating a low-memory trim and `CollectFull`.

Read it like this: in steady state a pool's `total` must stop growing — if it keeps climbing you are leaking instances (missing `Release`) or your warm-up count is too low; a scope that should be dead but still appears means something cached a reference across a load. Size warm-up counts and arena capacities from the peaks shown here, not guesses. Pair with the **Memory Profiler** package (already in this project) and the Profiler's *GC Alloc* column: gameplay frames should show **0 B** allocated.

### Timeline: what a snapshot cannot show

Press **Record** in the Inspector toolbar to start `MemoryRecorder`, and the Timeline pane fills in above the scope list.

The reason it exists is that the failures worth catching are *transitions*, not states. A pool registry wiped by a scene load, a scope that outlived the load which should have killed it, a pool that quietly stopped pooling and went back to Instantiate/Destroy — each of these looks fine in the frame you are looking at. The snapshot afterwards is clean and empty. Only something already recording can show you the moment it went wrong.

Three things to read there:

- **Escapes** — instances that reached `PoolBridge.Return` owned by no toolkit pool, so they were destroyed rather than pooled. Non-zero means pooling is not working and is costing more than not pooling at all. This is the number to drive to zero, and it is the regression baseline to capture *before* a migration (see `docs/INTEGRATION.md` §7).
- **Per-pool sparklines with a peak marker.** The peak is the warm-up count. The instantaneous active count a snapshot shows cannot size a pool; the high-water mark over a representative session can.
- **Events** — scope created/disposed, pool created lazily, trim, low memory. A scope bar that does not end where the level ended is a leak.

`MemoryRecorder` lives in the runtime assembly, so it also works in a player build. `MemoryOverlay.Show()` draws the same data on screen for a development build on device — which is where memory failures actually happen — and `MemoryRecorder.Dump()` writes a text report for device logs and CI. Both compile out entirely outside the editor and development builds, and a sampling tick allocates 0 B in steady state (asserted by a test).

The Inspector window is UI Toolkit; the on-device overlay is `OnGUI` and stays that way. `Show()` is the entire integration — no `PanelSettings` asset, no canvas, nothing to add to a scene — which is worth more on a device build than nicer curves are.

### Agent access (MCP)

**Window > Analysis > Memory Toolkit MCP > Enable Server** exposes the same capabilities to a coding
agent over MCP: `validate_prefab` / `validate_project`, live pool and heap state, recorder control and
the timeline (with `peakActive` per pool — the warm-up count — and derived findings), and, behind a
second opt-in, the mutating actions the Inspector's buttons perform (`warmup_pool`, `trim_pools`,
`dispose_scope`, `collect_full`).

The server runs *inside* the Editor because that is the only place the answers exist: the validator
reflects over compiled component types and reads deserialized prefab data, and the pool stats and
timeline are live process state. `Tools~/memory-toolkit-mcp/index.mjs` is a dependency-free stdio
bridge to it. See [`docs/MCP.md`](docs/MCP.md).

## The rules this codebase follows

1. **Group allocations by lifetime.** Everything belongs to a tier — Permanent, Scene, or Frame — and is released with its tier, never one-by-one at random times.
2. **Pre-allocate during loads.** `Warmup` and `FrameAllocator` capacity are sized from measured peaks (`PeakUsedBytes`, Memory Inspector window), not guesses.
3. **Recycle, don't destroy.** `maxSize` is set to the real peak so steady state never destroys an instance.
4. **No allocations in per-frame code.** Reused buffers (`GetComponentsInChildren(list)` overloads), cached delegates, no LINQ, no closures, no string concat, no `params`, no boxing (struct enumerators, no interface-typed foreach over structs).
5. **Transient data goes in the arena.** One contiguous block, bump-allocated, reset once per frame — fragmentation is structurally impossible there.
6. **Shrink on signal, not continuously.** Pools hold capacity until `Application.lowMemory`, then `Trim` to a floor and release unused assets.
7. **Blocking GC only behind loading screens.** Enable *Incremental GC* in Player Settings; call `MemoryManager.CollectFull()` only during transitions.
8. **Deterministic native memory.** `Allocator.Persistent` blocks are owned by disposable types and released in `Dispose`; nothing relies on finalizers.

## Adopting this in an existing project

[`docs/ADOPTION.md`](docs/ADOPTION.md) is the field guide for a project with **no pooling yet**: how to
triage an unfamiliar codebase for lifetime boundaries, what order to land the toolkit in, and the
pooling hazards that only show up in real projects (self-destructing FX prefabs, `AddComponent` spawn
paths, event-subscription leaks, async continuations outliving their scope).

[`docs/INTEGRATION.md`](docs/INTEGRATION.md) is the field guide for a project that **already pools**:
how to read an incumbent pool for its six usual failure modes, why the churn greps mislead you there,
and a migration order that never runs two registries blind. Its worked example is a shipped card game
whose incumbent pool had roughly 640 call sites.

Both are worked end to end on real production codebases, and each one's hazards were re-tested
against the other's project. Project and file names in both guides are generalized — the findings are
real, the code belongs to its owners.

## Samples

Import from Package Manager > Memory Toolkit > Samples. Each one is a real scenario mapped to a tier:

| Sample | Scenario | Tier |
|---|---|---|
| **Pooled Spawner** | Projectiles: get/release instead of Instantiate/Destroy; pool reaches steady state | Permanent or Scene |
| **Scene Scope Level** | A `LevelMemoryInstaller` warms the level's pools on Awake; everything is freed on scene unload; the one blocking `CollectFull()` happens behind the load | Scene |
| **Match Scope** | Rounds inside a persistent arena scene: unit pool + AI arena + native score table owned by a `CreateScope("Match N")`, freed by one `Dispose` at round end | Manual scope |
| **Frame Scratch Query** | Per-frame `OverlapSphereNonAlloc` with derived threat weights in `FrameScratch` — 0 B GC alloc per Update | Frame |
| **Zero-Alloc HUD** | `StringBuilderCache` label rebuilt only when values change | — |
| **Permanent Configs** | Login scene loads config assets and pins them to `Permanent`; the scene's own scope dies on unload while the configs survive `CollectFull` | Permanent + Scene |
| **Low Memory Response** | A decal cache that sheds old entries on `MemoryManager.LowMemoryTrimmed`, extending the automatic trim to game-side caches | — |
