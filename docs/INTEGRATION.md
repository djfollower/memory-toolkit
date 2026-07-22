# Integrating when the project already pools

[`ADOPTION.md`](ADOPTION.md) covers the greenfield case: a codebase with no pooling, where the job is
to introduce lifetimes. That guide's triage step 4 says "if the project already pools, you are
integrating, not adopting" and then moves on. This document is that missing branch.

The worked example is a shipped **card auto-battler**, roughly 6,000 C# files, about 2,300 of them in
the game assembly proper. As in `ADOPTION.md`, the findings are real and the names are generalized —
the code belongs to someone else. It was chosen because it is the *harder* normal case: not a team
that never pooled, but a team that pooled, shipped, and lived with the consequences for years. It has
an incumbent pool with roughly **340 "get from pool" and 300 "return to pool" call sites**.

The headline difference from the merge game in `ADOPTION.md`: there, the problem was that nothing
owned anything. Here, **three different things own overlapping subsets of the same objects**, and
none of them is a scope.

---

## 1. Triage for a brownfield project: find the incumbent first

Do not run the six greps from `ADOPTION.md` §1 yet. They assume the churn is visible as
`Instantiate`/`Destroy`, and in a project that already pools it isn't. This codebase has only ~50
`Instantiate` and ~90 `Destroy` sites across 2,300 files — which on the merge game's scale reads as a
clean codebase, and is completely misleading. The churn moved behind the pool's API.

**Grep for the pool's vocabulary before the engine's.** Four passes:

**1. Find the incumbent pool implementation.**
`grep -rlE "class .*(ObjectPool|PrefabPool|PoolManager|Pooler)\b" --include="*.cs"`
Here: a four-file folder, 195 lines total, through which every memory decision in the game routes.
Its core type is a vendored copy of Unity's `UnityEngine.Pool.ObjectPool<T>` with two additions.
Vendored-and-modified matters: the project no longer receives Unity's fixes to that type, and nobody
remembers it is a fork.

**2. Separate the pooling vocabulary from the domain vocabulary.** This trips up the count badly.
370 files mentioned "pool" — but it is a card game, so the recruit pool, hero pool, spell pool, and
card pool are *game rules* about which cards can be drawn. They have nothing to do with memory, and
they outnumbered the real hits.
`grep -rhoE "[A-Za-z]*Pool[A-Za-z]*" | sort | uniq -c | sort -rn` sorts this out in one pass.

**3. Count the *other* pooling systems.** There is never just one. This project ran three
concurrently:
- the GameObject pool, a global static registry;
- `UnityEngine.Pool.ListPool<T>`, used correctly, in the simulation layer;
- a VFX manager holding a `Dictionary<AssetReference, GameObject>` that is *not* a pool, sitting on
  top of the GameObject pool and calling into it.

**4. Find the migration debris.** `grep -rn "//.*GetFromPool"` — 16 commented-out pool calls, all at
sites since rerouted to the VFX manager's cache. Commented-out pool calls are an archaeological
record of a migration someone started. Read them before proposing a fourth system: they tell you
which way the team was already moving and what made them stop.

---

## 2. What the incumbent actually is

Read the incumbent's implementation in full before touching a call site. Here it is 195 lines and it
decides the following, none of it documented:

Every pool is keyed on `prefab.GetHashCode()` in a `static Dictionary`. Instances are stamped with a
small MonoBehaviour holding that int, so the return path can find its way home. Pools are created
lazily on first get; there is **no warm-up anywhere in the project** — the "add to pool" entry point
has zero call sites in game code. Every prefab's first spawn is an `Instantiate` during gameplay.

That design produces six failure modes, and this is the part worth generalizing, because a
hand-rolled pool that has survived to production almost always has some subset of them.

### A. The registry's lifetime is delegated to an unowned GameObject

The pool creates `new GameObject("[PoolRoot]")` in whatever scene happens to be active, and attaches
a MonoBehaviour whose entire body is:

```csharp
private void OnDestroy() => PoolRegistry.OnRootDestroyed();   // ...which clears every pool
```

It is never `DontDestroyOnLoad`. So a single-mode scene load destroys the root, which wipes the
static registry and destroys every *pooled* instance parented under it — while every *checked-out*
instance survives, still stamped with a hash code whose pool no longer exists. Those instances then
hit the fallback branch of the return path, which destroys the instance and logs a warning.

**That warning is not an anomaly — it is the routine steady state after any scene transition.** When
a team tells you their pool "logs a lot of warnings but works fine", this is usually the shape of it:
the pool has silently degraded into an `Instantiate`/`Destroy` path that costs more than no pool at
all, because it also pays for the registry lookups.

**Rule: a pool registry must not be owned by a scene object.** In toolkit terms, the registry *is* a
scope — `MemoryManager.Permanent` for cross-scene prefabs, `CreateSceneScope()` for scene-local ones.
The decision of which is a decision about the prefab, made once, not an accident of which scene was
active on first spawn.

### B. Returning without reparenting orphans the instance

The return method takes an optional "move to pool root" flag, defaulting to true. Fourteen call sites
pass false. Those instances are pushed onto the pool's stack and deactivated, but stay parented under
the view that was using them.

When that view is destroyed, Unity destroys the pooled instance with it. The pool's stack still holds
the reference. The next get pops a destroyed object. The pool does check for it and returns `null` —
but call sites do not, and the common idiom is a get immediately followed by `.GetComponent<T>()`, so
the failure surfaces as a `NullReferenceException` in a VFX path several scenes away from the return
that caused it. The pool's own instance counter is never decremented either, so its statistics say
the instance is still alive.

This is the same class of bug as the merge game's *Stop Action = Destroy* (`ADOPTION.md` §4) reached
by a completely different route, which is what makes it worth stating as a general invariant:

**Rule: a released instance must be reparented to a root the pool owns, unconditionally. Reparenting
is not a per-call-site option, because the call site does not know how long its own parent lives.**

### C. The double-release guard is O(n) and runs twice

The vendored pool does a linear `Contains` scan of its free list on every release when its collection
check is enabled — and it is hardcoded enabled, including in release builds. Unity's own pool leaves
this to the caller precisely because it is O(n).

The project's return wrapper then opens with its own "already returned?" check, which performs the
same linear scan. **Every return is two linear scans of the same stack.**

And because the pool's contract was never written down, 20 call sites guard it a third time by hand:

```csharp
// the guard the return method already performs internally
if (!someVfxObject.AlreadyReturned())
{
    someVfxObject.ReturnToPool();
}
```

The defensive idiom spread by copy-paste. It is redundant at every site, and it triples the cost of
the operation the pool exists to make cheap. Worse, the guard is *inconsistently* applied — within a
single 8,000-line view class, some of its 33 return calls are guarded and some are not, so a reader
cannot tell which sites are known-safe and which are merely untested.

**Rule: double-release safety belongs inside the pool, in O(1), and must be documented as a
guarantee — otherwise call sites will pay for it again, unevenly.**

### D. `?.` does not protect a Unity object

Fifteen call sites use the null-conditional operator on pooled objects:

```csharp
cachedVfx?.ReturnToPool();
cachedIdleVfx?.gameObject.ReturnToPool();
```

`?.` compiles to a reference-null test and **bypasses `UnityEngine.Object`'s overloaded `==`**, so a
destroyed-but-not-yet-collected object passes the check. Execution proceeds into the return path, hits
a `TryGetComponent` on a destroyed object, and lands in the destroy-and-warn fallback. The `?.` did
nothing except make the site look safe.

This is worth calling out separately from the general fake-null hazard because it is *invisible in
review*: `?.` is idiomatic, correct C#, and correct for every non-Unity type in the same file.

**Rule: at pool boundaries, never use `?.` on a `UnityEngine.Object`. Use `!= null` (which honours
the overload) or a `PooledRef<T>`, which answers the question the call site is actually asking —
"is this still my instance?" — rather than "is this pointer non-null?"**

### E. Pool identity derived from a loaded asset

The pool key is the *prefab's* hash code. For Addressables-loaded prefabs that identity is only
stable while the asset stays loaded. Release and reload the asset and you get a new key, a new empty
pool, and a population of live instances stamped with the old one — a slower-motion version of
failure A.

The VFX manager makes this concrete. Its cache holds loaded *prefabs* and then delegates to the pool,
while its removal path destroys the cached prefab without touching the pool registry, which still
holds a pool keyed on it.

The latent version of the same bug is one line away from firing. The async entry point does:

```csharp
GameObject prefab = await LoadAssetAsync<GameObject>(assetReference);
poolAssetRefs.Add(prefab.GetHashCode(), assetReference);   // throws on 2nd call
```

`Dictionary.Add` throws `ArgumentException` on a duplicate key, and the second call for the same
asset reference produces the same prefab and therefore the same key. That method currently has **zero
call sites**, so it has never fired — it is a trap armed for whoever adopts the async path next.

**Rule: key pools on something you own and control the lifetime of — a `ScriptableObject` reference,
a stable string id, or the scope's own handle — never on the runtime identity of an asset the loader
may release underneath you.** This is why the toolkit's Addressables integration gives the *scope*
the handle: the key and the asset then share a lifetime by construction.

### F. Teardown that ignores its own arguments

The cleanup method takes a pool and an instance, ignores both, and always disposes *everything*. It
is declared `async` but performs no await, and its only caller invokes it without awaiting and
without discarding the task explicitly. The asset-reference dictionary it iterates is never cleared,
so stale keys accumulate across every reload. And the vendored pool's `Clear()` destroys only what is
*in the stack* — checked-out instances are neither destroyed nor tracked — while resetting the
total-instance counter to zero, so after teardown the pool's own accounting reports zero objects and
an unknown number survive.

**Rule: a teardown that cannot be called on a subset is not a teardown, it is a reset. Scoped
disposal exists so that "release what this owns" is expressible at all.**

---

## 3. The scope that this project already invented

The most useful thing in this codebase is not a defect. It is that the team **needed scopes badly
enough to build one three times**, and each attempt is a precise specification of what they were
missing.

**Attempt 1 — a broadcast signal.** A "before reload game" signal has 14 hand-registered subscribers
across the codebase — the init flow manager, player data, the battle view, the drag-and-drop manager,
the payment service, the VFX base view, and eight more. It fires when the player has minimised the
game past a configured threshold and it must be torn back down to the login scene.

This is `scope.Dispose()` implemented as an event. It has exactly the two weaknesses the event form
always has: **no ordering guarantee** — subscribers run in registration order, which is scene-load
order, which nobody controls — and **no registration guarantee**, since each subscriber must remember
to both add and remove its own listener.

**Attempt 2 — a global cancellation token.** A 20-line static class whose whole content is a
`CancellationTokenSource` that gets cancelled, disposed, nulled, and *immediately replaced* on
reload. The init flow manager does the same dance a second time for another global token. A token
that is replaced rather than owned means any code holding the old token is now cancellable by nobody.

**Attempt 3 — copy-paste.** Ten view classes define their own private `CleanObjectsPool()`. Each is
the same loop over the same kind of list, returning children to the pool. Ten copies of a method is a
scope that was never given a name.

**This is the integration lever.** `ADOPTION.md` §3 Step 1 says "create the scope before you pool
anything" and justifies it on a codebase where nothing existed. In a brownfield project the argument
is stronger and easier to sell, because you are not asking for a new concept — you are asking to
replace three broken implementations of a concept the team already committed to. The reload path is
the scope boundary, it is already written down, and it already has 14 subscribers who will inherit
correct ordering for free (the toolkit disposes strict reverse-registration; `ADOPTION.md` §5.4).

---

## 4. Migration order for a brownfield project

Different from `ADOPTION.md` §3, because you cannot land a second pool alongside a live one and
leave both running indefinitely — you will get instances returned to the wrong registry.

### Step 1 — Make the incumbent's failures visible before changing anything

Do not start by replacing the pool. Start by proving how often it is already failing. The warning in
the return path's fallback is a free instrument: count it per session. Here it fires on the routine
path after every scene transition (failure A). A number — "the pool falls back to Destroy N times per
session" — is what makes the migration fundable, and it costs one afternoon.

### Step 2 — Own the registry's lifetime, keep its API

The smallest change with the largest effect is decoupling the registry from the pool root's
`OnDestroy`. Give the registry to `MemoryManager.Permanent` (or a scene scope, per prefab) while
leaving all ~640 call sites untouched. The existing get/return methods keep working; they just stop
being silently wiped mid-session. Independently shippable, independently revertable, no call-site
churn.

Concretely, this is a three-line change to the incumbent's extension methods — see
`MemoryToolkit.Migration.PoolBridge` and §6.1. Set `PoolBridge.UnknownInstances` to `LogAndDestroy`
first: it reproduces what the incumbent already does on an unrecognised instance, so day one is
behaviour-neutral and the only thing that changes is who owns the registry.

### Step 3 — Close the two correctness holes at the boundary, not the call sites

Both are one-line changes in the incumbent, and both are worth more than any call-site rewrite:

- Make the reparent unconditional — ignore the opt-out flag (failure B). Fourteen call sites change
  behaviour; none of them change text. Routing through `PoolBridge` does this for you:
  `GameObjectPool` always reparents on release.
- Make double-release detection O(1) and make the internal guard authoritative, then delete the 20
  redundant call-site guards (failure C). `GameObjectPool.Release` guarantees this, so the guards can
  go in the same commit that re-points the extension methods.

### Step 4 — Only now, migrate call sites — hottest first

One card view class alone has 17 gets and 33 returns and is the central object in a card game; the
battle view has 12 and 4. Those two files are the migration. The remaining ~570 sites are long-tail
UI and can run on the shimmed incumbent indefinitely.

### Step 5 — Collapse the redundant systems

The VFX manager's cache should not exist as a separate concept once the pool is scope-owned — it is a
prefab cache plus a pool call, and on a cache miss it performs a **synchronous blocking Addressables
load on the main thread, during battle**. That is a frame hitch with a pooling API wrapped around it.
Scope-owned async warm-up during the loading screen removes both the cache and the hitch.

### Step 6 — Per-frame allocations

Same position as `ADOPTION.md` §3 Step 3 — last, after pooling is stable. This project's per-frame
layer is markedly cleaner than the merge game's; the concentration is in drag-and-drop, which
allocates a `PointerEventData` and a `new List<RaycastResult>()` **every frame while dragging**
(four sites in the lineup manager, plus the card view), then calls `GetComponent<T>()` twice on the
same result. This is the textbook `FrameScratch` + cached-buffer case, and dragging a card is the
single most frequent player action in the game.

---

## 5. Does the greenfield hazard list survive contact with a second codebase?

Worth checking explicitly, because a hazard list derived from one project is a hypothesis, not a
finding. Against `ADOPTION.md` §4:

| Greenfield hazard | This codebase |
|---|---|
| ParticleSystem Stop Action = Destroy | **Confirmed.** 17 prefabs carry `stopAction: 2`, 8 of them under the ability-VFX folder — the exact prefabs that flow through the VFX manager into the pool. Reproduced independently, in a project that has pooled VFX for years. |
| `AddComponent` in the spawn path | **Confirmed, in the pool itself.** The get path does a `GetComponent` and a conditional `AddComponent` on *every* get. The guard prevents accumulation, but the hot path pays a `GetComponent` per spawn — precisely the cost the pool exists to avoid, and precisely toolkit gap `ADOPTION.md` §5.1. |
| `OnDestroy` work must move to `OnReturnedToPool` | **Confirmed and unresolved.** The incumbent has no `IPoolable` equivalent at all — no take/return callback exists, so per-instance reset is done ad hoc at call sites when someone remembers. |
| Identity-keyed collections | **Confirmed, and it is the pool's own key.** `GetHashCode()` on prefabs — the hazard is in the infrastructure rather than in game collections. |
| `async void` continuations crossing scopes | **Confirmed at boot scale.** The startup flow is a ~300-line `async` method with 15+ awaits that can be cancelled mid-flight by the reload path, against services it is concurrently constructing. |

Five for five, in a codebase with no relationship to the first — a different genre, a different
framework, and a different team. The list generalises.

The new entries this project adds — return-without-reparent (B), `?.` at pool boundaries (D),
asset-derived pool keys (E) — are all hazards that **only exist once a pool is present**, which is
why a greenfield project could not have surfaced them.

---

## 6. What real use said about the toolkit itself

Per `ADOPTION.md` §5, adoption is a two-way test. This project exercised the toolkit in a way the
greenfield one could not, and exposed four gaps of its own. Three are closed in 0.6.0; the fourth is
scoped below.

1. **There was no migration surface for an incumbent pool.** ~640 call sites spelled
   `gameObject.GetFromPool()` / `instance.ReturnToPool()` — extension methods on `GameObject` — cannot
   be rewritten in one change, and the toolkit offered no way to run underneath them. Without this the
   toolkit's honest answer to a brownfield project is "rewrite 640 sites first", which is the same
   answer as "no".
   → **`MemoryToolkit.Migration.PoolBridge`**: a backing implementation for a project's existing
   global pool API. The team re-points its handful of extension methods at the bridge and every call
   site keeps working, now on scope-owned pools:

   ```csharp
   public static GameObject GetFromPool(this GameObject prefab)  => PoolBridge.Get(prefab);
   public static void ReturnToPool(this GameObject instance)     => PoolBridge.Return(instance);
   public static bool AlreadyReturned(this GameObject instance)  => PoolBridge.IsPooled(instance);
   ```

   `PoolBridge.ScopeResolver` is where the *which scope owns this prefab* decision lives — the
   decision the incumbent made by accident (failure A). Defaulting to `Permanent` fixes the
   scene-wipe bug on day one; narrowing prefabs to a scene or match scope afterwards is what actually
   reclaims memory. This makes §4 Step 2 a landable change rather than a proposal.

2. **No guidance or detection for the two-registry period.** Any real migration runs the incumbent
   and the toolkit side by side for weeks, and nothing detected an instance from pool A being
   released into pool B — the symptom appears far from the cause.
   → **`GameObjectPool.Release` now throws on a foreign instance**, naming both prefabs, instead of
   failing obscurely inside the pool's accounting. And **`PoolBridge.UnknownInstances`** makes the
   policy explicit as a migration dial: `LogAndDestroy` (day-one parity with what most incumbents
   already do) → `Ignore` (while both registries are live) → `Throw` (once migration should be
   complete). `PoolBridge.UnknownInstanceCount` is the metric from §4 Step 1, watched to zero.

3. **Release semantics were not stated, so call sites re-implemented them.** The 20 hand-written
   guards in §2 C exist because the incumbent's contract was unwritten, and each one costs a linear
   scan.
   → **Double release is now a documented O(1) guarantee** on `GameObjectPool.Release`, counted in
   `DoubleReleaseCount`, with `PooledInstance.IsPooled` as the O(1) query an incumbent's equivalent
   can be re-pointed at. A guarantee that is not written down gets paid for at every call site.

4. **`maxSize` and warm-up guidance assumed someone chooses them.** Capacity here is whatever the
   *first* call site happened to pass — one site asks for 100, the default 10 applies everywhere
   else, for pools that may hold the same prefab. Lazy pool creation makes capacity a race between
   call sites, and `MemoryManager.GetPool(prefab)` had the same shape.
   → **`GameObjectPool.WasWarmedUp`**, surfaced in the Memory Inspector as *(not warmed)* and counted
   by `PoolBridge.LazyPoolCount`. A pool nobody warmed took its capacity from a guess and paid an
   Instantiate during gameplay to exist; that is now visible rather than silent.
   **`MemoryScope.TryGetPool`** additionally makes "is this prefab already pooled?" answerable without
   creating a pool as a side effect of asking.

### Still open: the call-site validator

`PoolSafetyValidator` checks prefabs, not the code that pools them. It would have caught the 17
`stopAction: 2` prefabs. It would not have caught failures B, D, or E — all of which are call-site and
infrastructure patterns, and all of which are statically greppable:

- `?.` applied to a `UnityEngine.Object` at a pool boundary (failure D)
- release paths that skip reparenting (failure B)
- pool keys derived from a loaded asset's runtime identity (failure E)

The prefab validator's premise — these bugs surface far from their cause, so catch them statically —
applies at least as strongly here. The obstacle is precision: these are source patterns rather than
asset data, so the check needs a real syntax pass to avoid the false positives that get a validator
switched off. Worth doing, not worth doing badly.

---

## 7. The brownfield checklist

Before integrating with an existing pool, answer these about the incumbent. Every one of them was a
live defect in the codebase above:

- [ ] What owns the pool registry, and what destroys it? If the answer is a MonoBehaviour's
      `OnDestroy`, that is the first fix.
- [ ] What is the pool key, and can the thing it is derived from be unloaded?
- [ ] Is reparent-on-release unconditional, or can a call site opt out?
- [ ] What happens on release into a missing pool — throw, log, or silently `Destroy`?
- [ ] Is double-release detection O(1), and is it documented well enough that call sites stop
      re-implementing it?
- [ ] Does teardown distinguish "everything" from "what this scope owns"?
- [ ] Are checked-out instances tracked, or only the free list? What does teardown do with them?
- [ ] Is there a take/return callback (`IPoolable`) at all, or is per-instance reset ad hoc?
- [ ] How many *other* caching systems wrap this one, and does any of them load synchronously?
- [ ] How often does the pool's own fallback/warning path fire in a real session? Measure before
      migrating — it is both the business case and the regression baseline.
