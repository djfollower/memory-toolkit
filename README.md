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

## The rules this codebase follows

1. **Group allocations by lifetime.** Everything belongs to a tier — Permanent, Scene, or Frame — and is released with its tier, never one-by-one at random times.
2. **Pre-allocate during loads.** `Warmup` and `FrameAllocator` capacity are sized from measured peaks (`PeakUsedBytes`, Memory Inspector window), not guesses.
3. **Recycle, don't destroy.** `maxSize` is set to the real peak so steady state never destroys an instance.
4. **No allocations in per-frame code.** Reused buffers (`GetComponentsInChildren(list)` overloads), cached delegates, no LINQ, no closures, no string concat, no `params`, no boxing (struct enumerators, no interface-typed foreach over structs).
5. **Transient data goes in the arena.** One contiguous block, bump-allocated, reset once per frame — fragmentation is structurally impossible there.
6. **Shrink on signal, not continuously.** Pools hold capacity until `Application.lowMemory`, then `Trim` to a floor and release unused assets.
7. **Blocking GC only behind loading screens.** Enable *Incremental GC* in Player Settings; call `MemoryManager.CollectFull()` only during transitions.
8. **Deterministic native memory.** `Allocator.Persistent` blocks are owned by disposable types and released in `Dispose`; nothing relies on finalizers.

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
