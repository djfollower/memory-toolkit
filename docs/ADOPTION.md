# Adopting Memory Toolkit in a production project

The README describes the API. This describes the *method* — how to walk into an existing Unity
codebase, find the memory that matters, and land the toolkit in an order that pays off early and
never breaks the game.

The worked example throughout is a shipped **merge/drop puzzle game**, roughly 530 C# files of game
code. Every observation below is real, taken from that codebase; project, studio, and file names are
generalized because the code is not ours to publish, but nothing about the findings is invented. It
was chosen because it is the normal case, not the clean one: about 120 `Instantiate` calls, about 240
`Destroy` calls, and exactly one file out of ~530 that mentions pooling at all.

> For a project that **already has a pool**, stop here and read
> [`INTEGRATION.md`](INTEGRATION.md) instead — the triage below will mislead you.

---

## 1. Triage: six greps, in this order

Do not start by reading code. Start by finding the *lifetime boundaries*, because the toolkit's
whole model is that memory is grouped by lifetime. Six passes, roughly 20 minutes:

**1. Find the boot entry point → the Permanent boundary.**
In the merge game it is an app-loader MonoBehaviour sitting in the first scene. It instantiates a
project-context prefab, registers a log handler, and on an "app initialized" callback binds services
and pushes the first state. Everything reachable from there that is never torn down is **Permanent**
tier — in this project, 26 installers covering config services, addressables, audio, and currency.

**2. Find the session boundary → the Scene tier.**
Look for the class with a big `OnDestroy`/`Dispose` that tears down a play session.
Here it was the gameplay manager's `OnDestroy`, which disposes the piece manager, goal service,
boost service, and expression controller. That method *is* the scope boundary — it already exists,
someone maintains it by hand, and every line in it is a candidate for `scope.Register`.

**3. Count the churn.** `grep -rc "Instantiate(\|Destroy(" --include="*.cs"`.
Here: ~120 / ~240. A `Destroy` count roughly double the `Instantiate` count is the signature of a
consume-two-produce-one game loop — which is exactly what merge is.

**4. Check for existing pooling.** If the project already pools, you are integrating, not adopting —
stop here and switch to [`INTEGRATION.md`](INTEGRATION.md), which covers the brownfield case (and
warns that in a project with an incumbent pool, greps 3 and 5 below will badly understate the churn).
In the merge game exactly one file mentioned `Pool`. Effectively greenfield.

**5. Find the innermost gameplay loop.** Not the busiest file — the one that runs per player action.
Here it is the merge itself: the merge sequence controller destroys two pieces, creates one, and
spawns a particle effect. Every merge is 2 destroys + 2 instantiates, and a player does this hundreds
of times per level.

**6. Read every `Update()`/`FixedUpdate()` body.** There are usually fewer than you fear.
Here: 21 `Update`, 2 `FixedUpdate`. Small enough to read all of them in one sitting — do it, because
per-frame allocation is invisible in code review and obvious in the Profiler.

The output of triage is not a fix list. It is a **scope map**.

---

## 2. The scope map

Assign every lifetime before writing any code. For the merge game:

| Tier | Owner | Contents |
|---|---|---|
| **Permanent** | app loader / installers | Config services, addressables catalogs, audio manager, UI configs. Pin configs that outlive the boot scene (`Permanent.Pin`). |
| **Scene** | gameplay manager | Everything `OnDestroy` currently disposes by hand: piece pools, piece-modifier addressable handles, goal/boost services. |
| **Frame** | depenetration and physics queries | Per-piece overlap and raycast scratch buffers. |

The rule that resolves most arguments: **ownership follows lifetime, not the class that did the
loading.** The piece manager loads piece-modifier configs via Addressables and releases them in its
own `Dispose` — correct today, but it is hand-rolled refcounting that breaks the moment someone adds
an early-return. Give the scope the handle (`sceneScope.LoadAssetAsync<PieceModifierConfig>(guid)`)
and the release becomes structural.

---

## 3. Adoption order

Land these in sequence. Each step is independently shippable and independently revertable.

### Step 1 — Scene scope first, pooling second

Counterintuitive but important: **create the scope before you pool anything.** A scope with nothing
in it still pays for itself, because it converts the gameplay manager's `OnDestroy` from a checklist
into an invariant. Pools added later inherit correct teardown for free.

```csharp
// At level start:
_sceneScope = MemoryManager.CreateSceneScope();

// OnDestroy becomes:
_sceneScope.Dispose();
```

### Step 2 — Pool the one prefab that churns most

Not all 120 call sites. One. In the merge game that is the piece, whose clone and destroy paths look
roughly like this:

```csharp
// the clone path today
public IPiece Clone() => Instantiate(this);

// ...and the destroy path
public virtual void Destroy(/* ... */)
{
    /* ... */
    UnityEngine.Object.Destroy(gameObject);
}
```

Route both through a pool owned by the scene scope, warmed during the level load. Measure, ship,
then widen. Piece-destroy FX (an instantiate plus a timed `Destroy`) and merge FX are the natural
second and third.

### Step 3 — Per-frame allocations

Only after pooling is stable. These are small but they are the difference between "low GC" and
"0 B/frame". The four found here were:

- A timer label building `$"{minute}:{second:00}"` **every frame** the timer runs. This is the
  textbook `StringBuilderCache` case, and it should additionally only rebuild when the displayed
  integer second changes.
- `new WaitForSeconds(...)` allocated per dropped piece. Cache one static instance; `WaitForSeconds`
  is immutable and safely shared.
- A depenetration component allocating a `Collider2D[]` and a `RaycastHit2D[]` **per piece**. With
  pooling these survive reuse; without pooling they are pure churn. Frame-lifetime derived data
  belongs in `MemoryManager.FrameScratch`.
- Three `.ToList()` calls on dictionary keys in the piece manager. Use `ListPool<T>`. (Two of the
  three were editor-only, so the third was fixed first.)

---

## 4. Gotchas this project actually surfaced

These are the parts that are not in any pooling tutorial. Every one of them came out of a real
codebase rather than from theory.

All five were independently reproduced in a second, unrelated production codebase — see
[`INTEGRATION.md`](INTEGRATION.md) §5 — which also adds three hazards that only appear once a project
already has a pool.

### A ParticleSystem prefab with Stop Action = Destroy cannot be pooled

One FX prefab had Stop Action set to Destroy (`stopAction: 2` in the prefab YAML). The particle
system deletes its own GameObject when it finishes. Pool it unchanged and the pool hands out
destroyed instances: `Get()` returns a reference to a dead object, and Unity's fake-null makes the
failure surface far from the cause.

**Rule: before pooling any FX prefab, set Stop Action to Disable (`stopAction: 1`) and release it
from an `IPoolable`/`OnParticleSystemStopped` callback instead.** Audit the whole prefab, including
child systems — one child still set to Destroy takes the parent's hierarchy with it.

This generalizes: pooling requires that *nothing inside the prefab destroys itself*. Grep pooling
candidates for `Destroy(gameObject)`, `Destroy(this)`, and Stop Actions before you pool them.

### `AddComponent` per instance defeats the pool

The piece setup path added a drag component and a depenetration component to every new piece; the
piece's own `Awake` added an expression handler; modifiers added more. `AddComponent` allocates
and — critically — **has no cheap inverse**. Pool an instance that has been through setup twice and
it carries two copies of each.

**Rule: pooled prefabs get their components at author time, not at spawn time.** Convert
`AddComponent<T>()` to a serialized reference plus `Init(...)`, and re-`Init` in `OnTakenFromPool`.
Where components genuinely vary per instance (here, piece modifiers chosen by config), keep the
`AddComponent` but pool **per configured variant**, not per base prefab — otherwise every `Get()`
needs a teardown pass that costs more than the instantiate you saved.

This is usually the single largest refactor in adopting pooling, and it is worth scoping explicitly
before committing to a sprint.

### Reset is not "set fields to default" — it is "undo every subscription"

Piece setup subscribed the new piece to a merge event and to a game-over trigger. The piece's
`OnDestroy` did the cleanup: `DOKill()` on the transform, disposing attached modifiers,
unregistering expression changes.

With pooling, `OnDestroy` **stops running**. Everything in it must move to `OnReturnedToPool`:

1. Unsubscribe every event the instance subscribed to, and every event *others* subscribed to on it.
2. `transform.DOKill()` — a live DOTween on a pooled transform will animate the *next* user's piece.
3. `StopAllCoroutines()` — the piece had a delayed enable coroutine; a pooled instance re-taken
   within the delay window gets two of them.
4. Reset physics: velocity, angular velocity, `isKinematic`, `gravityScale`. The merge controller's
   freeze step mutates all four, and a pooled piece that was frozen mid-merge comes back kinematic.
5. Clear the attached-modifier and layout-modifier collections.

**Rule: a pooled type's `OnReturnedToPool` should be reviewable against its old `OnDestroy` line by
line.** If your codebase's `OnDestroy` was doing real work, that work does not disappear — it moves.

### Identity keyed on `GetHashCode` is a pooling hazard

The piece manager keyed its live-piece dictionary on `piece.gameObject.GetHashCode()`. This works
today because instances are unique. Under pooling the same GameObject is registered, removed, and
registered again over a level's lifetime — correct **only if removal is exact**. One missed
`Release`-path removal and the next `Get()` throws a duplicate-key `ArgumentException` on the add,
which reads as a bizarre unrelated crash.

**Rule: before pooling, audit any collection keyed by instance identity for exact add/remove
pairing.** Prefer `GetInstanceID()` over `GetHashCode()`, and prefer registering on
`OnTakenFromPool`/`OnReturnedToPool` over registering at call sites.

### Async lifetimes cross scope boundaries

A modifier-loading method was `async void`, awaited an Addressables load, then called
`AddComponent` on the piece. If the level ends mid-await, the continuation runs against a disposed
scope and a destroyed (or worse, *pooled and reused*) piece.

**Rule: after every `await` in gameplay code, re-check both the scope and the target instance before
touching them.** `scope.IsDisposed` exists for exactly this. A pooled instance additionally needs a
generation/version counter — "is this still the same occupant of this object?" — because a null check
passes on a reused instance.

---

## 5. What real use said about the toolkit itself

Adoption is a two-way test: friction hitting real code is feedback about the API, not just about the
project. This project exposed four gaps, all closed in 0.5.0.

1. **`GameObjectPool` was GameObject-typed; game code is component-typed.** The game's `Clone()`
   returned an interface type; the pool returned `GameObject`, forcing a `GetComponent` at every call
   site — in the hot path the pool exists to optimize.
   → **`pool.Get<T>()` / `pool.Release(component)`**, with the lookup resolved once per instance and
   cached on its `PooledInstance`. `Get<T>` throws on a prefab with no `T` instead of returning a
   null to be dereferenced later.
2. **No pooling-readiness diagnostic.** Most of §4 is statically detectable before the first play
   session, which matters because these bugs surface far from their cause.
   → **`PoolSafetyValidator`** (Assets > Memory Toolkit > Validate Pool Safety), covering Stop Action
   = Destroy including child systems, `OnDestroy` doing cleanup, rigidbodies with no `IPoolable`
   reset, missing scripts, and the `Awake`/`OnEnable` semantics that change under reuse. It reads
   prefab data and type metadata, not method bodies — so `Destroy(gameObject)` inside a script still
   has to be caught by the §6 checklist.
3. **`IPoolable` didn't express identity generation**, so the async-reuse hazard had to be solved
   per-game.
   → **`PooledRef<T>` + `PooledInstance.Generation`**. Non-pooled components are supported and are
   alive while non-null, so call sites don't branch on whether their target is pooled.
4. **Scope teardown order was unspecified.** The game's `OnDestroy` disposes in a deliberate order
   (pieces before the services they call into). Pools were always disposed before registered
   disposables regardless of registration order, so a hand-ordered teardown could not safely be
   replaced by `scope.Dispose()` — Step 1 of adoption, and therefore the first thing anyone asks.
   → **Dispose is now strict reverse-registration (LIFO) across pools, arenas, and disposables
   alike**, and documented as a guarantee. Register in dependency order and the ordering carries
   over. Note `GetPool` registers on first use, so a lazily-acquired pool registers later — and is
   therefore disposed earlier — than one warmed up front.

---

## 6. The checklist

Before pooling any prefab:

- [ ] No `Destroy(gameObject)` / `Destroy(this)` in its scripts or children
- [ ] No ParticleSystem Stop Action = Destroy, including child systems
- [ ] No `AddComponent` in its spawn path (or pool per configured variant)
- [ ] `OnDestroy`'s work has moved to `OnReturnedToPool`, reviewed line by line
- [ ] All event subscriptions symmetric across take/return
- [ ] Tweens killed, coroutines stopped, physics state reset on return
- [ ] Identity-keyed collections have exact add/remove pairing
- [ ] Post-`await` code re-checks scope and instance validity
- [ ] `maxSize` set from a measured peak, warm-up count sized from the Memory Inspector's Timeline
      **peak active** over a representative session — not guesses, and not the instantaneous count
      a snapshot shows, which is whatever happened to be live when you looked
