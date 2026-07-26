# Changelog

## [1.0.0] - 2026-07-26

The toolkit's best asset was never the API — it was the *method* in the two field guides: how to walk
into an unfamiliar Unity codebase and find the memory that matters. The first step of that method is
mechanical, and until now it lived only as prose a person had to read. This release runs it as code,
and with it the package covers the whole arc: measure → configure → enforce → observe in the field →
triage the next project.

### Added
- **`triage_project`** (MCP) — the adoption triage of `docs/ADOPTION.md` §1 as data: Instantiate/
  Destroy churn and its ratio, the Update/LateUpdate/FixedUpdate census, boot-entry and session-
  boundary candidates, the hottest-churn file, and incumbent-pool detection that branches to the
  ADOPTION or the INTEGRATION guide automatically. Backed by `ProjectTriage`, which depends only on
  `System.IO` and regex so the shipped code can be pointed at any directory.
- **`propose_scope_map`** (MCP) — a draft of the §2 Permanent / Scene / Frame map from the triage,
  each tier carrying a confidence and the evidence behind it.
- **`suggest_budget`** (MCP) — the recorded timeline's peak-active per pool as a draft `MemoryBudget`,
  grouped by scope.
- **`explain_finding`** (MCP) + **`FieldGuideIndex`** — maps a validator issue, timeline anomaly, or
  analyzer rule (MTK001/MTK002/MTK007) to the guide section that says why it breaks and what to do.
  The seam that makes the agent's method the documented method.

### Notes
- **Acceptance was a run against the two real codebases the guides are built on**, not a synthetic
  scene. Cold, the shipped triage reproduces their documented scope maps: the greenfield merge game
  resolves to ADOPT (568 files, 121 Instantiate, exactly 1 file mentioning a pool, `AppLoader` as the
  Permanent owner, `GameplayManager` among the session boundaries); the brownfield card game resolves
  to INTEGRATE (`GameInitFlowManager` as the boot entry, ~400 pool-mentioning files detected as the
  incumbent). The one imperfect number is the Update census — the regex counts every `void Update(`
  under `Assets`, so it runs higher than the guide's hand-count of gameplay Updates; FixedUpdate
  matched exactly. Triage is heuristic and labelled as such: every candidate carries its file and
  line, and assigning the final lifetimes stays the human decision.
- The guides are referenced by section, not embedded as MCP resources, so the server stays tool-only
  with nothing extra to keep in sync.

## [0.13.0] - 2026-07-26

Every diagnostic so far stopped at the editor or at a pane a human watches. Memory failures do not
happen there — they happen after forty minutes on a low-end phone, to a QA tester who cannot read an
overlay, or to a player whose OS kills the app and sends a stack trace with no memory context. This
release takes the toolkit off the desk.

### Added
- **`MemorySoak`** — writes a session report to disk on an interval during an unattended run, in the
  **same schema the CI gate writes**, so an overnight device soak becomes an artifact the existing
  reader parses. `Begin(intervalSeconds)` defaults to `persistentDataPath/mtk-soak`, names files by
  UTC timestamp, keeps the most recent 20, and writes on pause and quit so the last report is closest
  to the failure. `DumpNow()` for a report at a known moment; `Stop()` to end it.
- **`MemoryBreadcrumbs`** + **`IBreadcrumbSink`** — pushes a fixed, budgeted set of memory facts
  (escapes, live scopes, busiest pools, managed heap, low-memory count) into a crash reporter, so an
  OOM arrives with a postmortem attached. Wired to `Application.lowMemory` automatically, captured
  before the trim so it records the state that triggered the warning.
- **`MemorySessionReport`** (runtime) — the session-report JSON now has a single definition in the
  runtime assembly; `MemoryToolkitCI.WriteSessionReport` delegates to it, so a device dump and a CI
  report can never drift out of a shared reader's reach.
- **`MemoryRecorder.DumpToFile(path)`** — the text report to a file, for a device where the log is
  hard to reach but the filesystem is not.
- **`Samples~/DeviceSoak`** — a `DeviceSoakBootstrapper` and a Crashlytics `IBreadcrumbSink`, wired
  end to end. Copy it, point one line at your reporter, and a dev build is instrumented.
- **[`docs/DEVICE.md`](docs/DEVICE.md)**.

### Notes
- The breadcrumb key set is a fixed handful (under ten keys, each under 1 KB) on purpose. Crash
  reporters cap custom keys hard (Crashlytics: 64 keys × 1 KB) and silently truncate an over-cap
  payload — dropping exactly the fields you added. So breadcrumbs send a "busiest pools" summary, not
  a key per pool; the per-pool detail lives in the soak file.
- The toolkit ships **no analytics dependency**. `IBreadcrumbSink` is one method and the Crashlytics
  adapter is a sample.
- Soak and breadcrumbs compile out entirely outside the editor and development builds. Every writer
  swallows its own exceptions — a diagnostic that crashes the run it observes is worse than one that
  quietly fails.

## [0.12.0] - 2026-07-26

The field guides describe call-site failures in prose, and prose does not survive contact with a
thirty-person team. This release moves the highest-confidence rules into a Roslyn analyzer, where they
carry an ID that can be suppressed per line, tracked in review, and enforced at the moment of writing
rather than at review.

### Added
- **`MemoryToolkit.Analyzers`** — a Roslyn analyzer DLL Unity loads automatically (the
  `RoslynAnalyzer` label is set; nothing to install). Three rules:
  - **MTK001** (Warning, on) — `?.`, `??`, `??=` and `is null` on a `UnityEngine.Object`. Each
    compiles to a reference comparison that skips Unity's overloaded `==`, so a destroyed object
    passes as alive. The most common bug at a pool boundary, and invisible in review because it is
    correct C# for every non-Unity type in the same file.
  - **MTK002** (Warning, on) — allocation in `Update` / `LateUpdate` / `FixedUpdate` on a
    MonoBehaviour: collections, arrays, string interpolation/concatenation, LINQ, and Unity yield
    instructions. Deliberately does not flag structs (`new Vector3(...)`) or `Update` on a non-
    MonoBehaviour.
  - **MTK007** (off by default) — a `UnityEngine.Object` used after an `await` without a re-check,
    the pooled-reference-outlives-its-owner hazard.
- **[`docs/ANALYZER.md`](docs/ANALYZER.md)** — rules, the `.editorconfig` knobs, and the measured
  false-positive results the on/off split is based on.

### Notes
- **The default split is empirical, not a judgement call.** Measured against the two production
  codebases the field guides are built on: MTK001 found 16 (greenfield) and 761 (brownfield), MTK002
  found 4 and 7 — every reviewed finding a true positive, including a file that guards `_timeline`
  with `if (_timeline != null)` on one line and `_timeline?.Play()` on another. MTK007 found 42 and
  818, which is not a bug rate — it measures how often code touches a Unity object after an await,
  most of it fine. On by default it would get the whole analyzer switched off within a day and take
  MTK001/MTK002 with it, so it ships off.
- Built against netstandard2.0 and Roslyn 3.8 on purpose: Unity loads analyzers into its own Roslyn
  host, and building against a newer compiler than the host provides is the usual cause of an
  analyzer that is silently ignored. Verified loading and warning inside Unity 6000.3, not just in
  unit tests.
- Source and tests live in `Analyzers~/` (the trailing tilde keeps Unity from importing the C#
  project as game code); the shipped artifact is `Analyzers/MemoryToolkit.Analyzers.dll`.

## [0.11.0] - 2026-07-23

`CreateSceneScope()` assumed scenes are how a project ends things, and that a scope can be created
after the scene is known. Neither holds for an Addressables scene load, a bespoke flow manager, an
additive UI stack, or a match that ends without a scene change — and a lifetime this package cannot
express is one somebody hand-rolls beside it. Two systems that disagree about ownership leak worse
than one that never existed.

### Added
- **`MemoryToolkit.VContainer`** — `builder.RegisterMemoryScope("Level")`. The scope is registered
  for injection and disposed by the container. Both halves matter: VContainer's `RegisterInstance`
  does **not** transfer ownership, so registering a scope and assuming it gets torn down leaks all of
  it silently.
- **`MemoryToolkit.Zenject`** — `Container.BindMemoryScope("Level")`. Makes two bindings, one for
  injection and one for the disposal pipeline; binding only the first resolves perfectly and leaks
  the scope, so the adapter does both rather than leaving it to the caller to remember.
- **`scope.AttachTo(GameObject)`** — dies with the object. Attaching a second scope to the same host
  throws rather than overwriting, because a silent overwrite drops the first scope's disposal with no
  symptom at the call site.
- **`scope.AttachTo(Scene)`** — for when the scope exists before the scene does, which is the normal
  order with Addressables scene loads.
- **`scope.DisposeWhen(subscribe, unsubscribe)`** — bind disposal to an event the project already
  has. Unsubscribes on disposal either way round, so a scope disposed early does not stay alive on an
  event that outlives it.
- **`scope.OnDisposed(Action)`** — teardown notification. Runs immediately if the scope has already
  been disposed: integration code subscribes from outside, so "it already ended" is a race rather
  than an error, and never firing would leave the subscriber waiting on an event that has been and
  gone.
- **[`docs/INTEGRATIONS.md`](docs/INTEGRATIONS.md)**.

### Notes
- Both adapters are optional assemblies with **no hard dependency**, using the same version-define
  pattern as the Addressables assembly. Install the container and the adapter compiles; don't and it
  does not exist.
- Contrary to what the roadmap assumed, these **are** covered by the test suite:
  `Tests/Integrations/` holds a test assembly per container, gated on the same define, verified
  against VContainer 1.16.5 and Extenject 9.2.0. The case each one asserts is the one that leaks
  silently — that disposing the container really does dispose the scope and its pools.
- `MemoryScopeAnchor` is `[ExecuteAlways]`. Without it, MonoBehaviour messages do not run outside
  play mode, so an anchor attached from an editor tool would never fire.

## [0.10.0] - 2026-07-23

Every entry point in this package was a human in an Editor session — a menu item, a window, a Record
button. A studio's workflow is a build farm, a QA matrix, and people who will not read a field guide.
Nothing here produced or consumed an artifact, so it could not participate in any of that. This
release adds the two artifacts: numbers a non-programmer can edit, and a gate a build machine can
fail.

### Added
- **`MemoryBudget` asset** (Create > Memory Toolkit > Memory Budget) — warm-up counts, max sizes,
  arena capacities and heap ceilings as data instead of literals in an installer, **tiered by device
  class**. A single warm-up number is wrong on two of any three targets, which is the usual reason a
  tool like this gets partially reverted. `scope.ApplyTo(budget)` replaces a hand-written installer;
  `budget.ApplyGlobals()` sets the process-wide numbers.
- **`TieredInt` and `DeviceTier`** — every number carries a Low/Medium/High value. Fill in `High`
  only and it means one number everywhere; a zero resolves to the tier above, so a partially filled
  row is always coherent. `DeviceTier.Current` is cached, because a tier that can change between two
  pools in one scene produces a configuration nobody tested. Supply
  `DeviceTier.Provider` to use a real device database; the built-in one reads `SystemInfo` and is
  deliberately crude.
- **Apply measured peaks** (budget inspector) — writes each recorded pool's peak active count into
  the matching entry. Sizing a pool was always a measurement whose last step was a human copying a
  number off a chart, which is where the loop broke: done once at adoption and never again. Recorded
  pools with no entry are reported rather than added — which scope owns a pool is a decision, not a
  measurement.
- **Batch-mode gate**: `-executeMethod MemoryToolkit.Editor.CI.MemoryToolkitCI.Validate`, writing a
  versioned JSON report and JUnit XML, exiting non-zero on findings. Two gates, separated on purpose:
  the **static** one reads asset data only — no play mode, no graphics device, nothing the studio has
  to build — so a team with no automated play sessions still gets value on day one. The **dynamic**
  one (`WriteSessionReport`) asserts escapes and heap ceilings over a play session the project
  supplies.
- **`MemoryBudgetAudit`** — static coherence checks on a budget: a warm-up above its max size on any
  tier (the pool would destroy what it just created, on the loading screen, silently), a direct
  prefab reference in a non-Permanent scope, empty entries, duplicate scopes, entries that do
  nothing, inverted tiers, and **a budgeted prefab that fails pool safety** — the check that connects
  the budget to the thing it configures.
- **[`docs/BUDGETS.md`](docs/BUDGETS.md)** and **[`docs/CI.md`](docs/CI.md)**, with working GitHub
  Actions and Jenkins snippets.

### Changed
- **The project-wide prefab sweep moved out of the MCP `validate_project` handler** into
  `PoolProjectScan`, shared by MCP and the CI gate. Three copies of a validator's entry point drift,
  and the symptom is CI and the agent disagreeing about whether the project is clean. Tool output is
  unchanged.

### Notes
- A budget holding **direct `GameObject` references pins those prefabs** and their meshes, materials
  and textures for as long as the budget is loaded — a budget listing every level by direct reference
  loads every level at boot. Direct references are for Permanent-tier content only; everything else
  belongs behind an addressable key, which this asset holds as a string and cannot accidentally load.
  The audit flags the mistake.
- `ApplyTo` warms direct references and hands keyed entries back in `ApplyResult.PendingAddressables`
  rather than growing an Addressables dependency in the runtime assembly to make an API look
  complete.
- Start the gate at `-mtk-fail-on never`. The first run on a real project finds a backlog, and
  failing the build on day one gets the gate switched off permanently by someone with a release to
  ship.
- Exit codes are `0` clean, `1` findings, `2` the gate itself failed. `-quit` alone exits 0 no matter
  what happens, so a crashed gate would otherwise be indistinguishable from a passing one.

## [0.9.0] - 2026-07-23

Everything in this package could only measure pooling *after* someone had already done the work of
adopting it — pool stats require pools. But adopting pooling in a real project is a sprint, and a
sprint has to be argued for with a number nobody could produce yet. This release closes that gap
from the other side: measure what pooling would save, while pooling nothing.

### Added
- **`PoolBridge.Mode = PoolBridgeMode.Observe`** — shadow mode. The bridge instantiates and destroys
  exactly as the un-pooled code did; no pool is created and nothing is recycled, so the change is
  safe to land and ship to a playtest on its own. Meanwhile `PoolShadow` counts, per prefab, the
  instantiates and destroys a pool would have absorbed and the **peak concurrent live count** — the
  warm-up size, and the one number a pre-migration codebase cannot otherwise produce.
- **`PoolShadow.Report()` / `ReportJson()`** — the projection as text for device logs and tickets, or
  as versioned JSON for CI artifacts and for seeding warm-up counts. `MemoryRecorder.Dump()` folds
  the report in, because on device the log is the only channel a shadow run has.
- **Shadow rows in the Timeline.** `PoolShadow` reports its prefabs in the same shape as pool stats,
  so the Inspector's per-pool sparkline and peak marker draw them with no special case. On a shadow
  row the peak marker *is* the warm-up count.
- **`MemoryEventKind.ShadowModeEnabled` / `ShadowModeDisabled`**, drawn in a distinct colour: a
  reader must not mistake a stretch of timeline where nothing was pooled for one where it was.

### Notes
- Observe mode is **refused outside the editor and development builds** — it costs an `AddComponent`
  per instance to attribute returns, so shipping it on would be a regression caused by a memory tool.
  The setter logs an error and stays in `Active` rather than trusting a build configuration.
- It also makes `PoolBridge` useful to greenfield projects, which previously had no reason to touch
  it: route `Instantiate`/`Destroy` pairs through the bridge in Observe mode, measure a real session,
  and only then decide what to pool. The bridge is now the general adoption seam, not a
  brownfield-only shim.
- Switching modes mid-session is safe: an instance a pool still owns is released to its pool rather
  than destroyed under it.
- Double returns and returns of instances created outside the bridge are counted and called out in
  the report — the first is a defect to fix before pooling, where it corrupts a free list; the second
  means the projection is a floor rather than a total.

## [0.8.0] - 2026-07-23

Adopting pooling is a loop — *is this prefab safe to pool, is it pooled now, how big should the pool
be, is anything still escaping* — and until now an agent working in a project could not close it. It
could read the source and guess. Every one of those questions is answered by data that exists only
inside a running Editor, so this release puts the tools there and speaks MCP.

### Added
- **MCP server in the Editor** (`Editor/Mcp/`, `Window > Analysis > Memory Toolkit MCP`). Eleven
  tools: `editor_status`, `validate_prefab`, `validate_project`, `get_pool_stats`,
  `get_memory_snapshot`, `recorder_control`, `get_recorder_timeline`, and — behind a separate opt-in —
  `warmup_pool`, `trim_pools`, `dispose_scope`, `collect_full`. `get_recorder_timeline` returns
  `peakActive` per pool as `suggestedWarmupCount` and derives findings (pools created lazily during
  gameplay, instances escaping the pool) rather than leaving a model to infer them from raw samples.
  Off by default; loopback-only, with a per-session token, and the mutating tools need a second
  opt-in. Requests are queued onto the main thread and time out with an explanatory error rather than
  hanging a tool call while the Editor compiles.
- **`Tools~/memory-toolkit-mcp`**: dependency-free Node stdio bridge (Node 18+). The tool list is
  fetched from Unity rather than duplicated, so a tool cannot drift from its description; when the
  Editor is closed the bridge serves its cached list and announces `tools/list_changed` once it
  returns, because a client reads the tool list once, at connect.
- **[`docs/MCP.md`](docs/MCP.md)**: setup, tool reference, the adoption loop the tools are shaped for,
  and the trust boundary.

## [0.7.0] - 2026-07-22

A snapshot cannot show a transition, and every memory failure this package exists to prevent is a
transition: a registry wiped by a scene load, a scope that outlived the load which should have killed
it, a pool that quietly stopped pooling. Each looks fine in the frame you are looking at, and the
snapshot taken afterwards is clean and empty. This release adds the time axis.

### Fixed
- **`GameObjectPool.CountActive` could report a negative count, and stayed wrong for the rest of the
  session.** It forwarded to `ObjectPool<T>`, which derives active as `CountAll - CountInactive`;
  `Clear()` zeroes `CountAll` while instances are still checked out, and `Trim(keep > 0)` clears
  internally as part of its partial-trim path. Trimming a pool that retained 2 instances reported
  `CountActive == -2`. The Memory Inspector's own Trim button triggered it. Both counts are now
  summed from the tracked active set rather than derived. If you were logging these numbers, they
  change — the old ones were wrong.

### Added
- **`MemoryToolkit.Diagnostics.MemoryRecorder`**: a fixed-capacity recorder of pool and scope activity
  over time. Two streams — sparse *events* (scope created/disposed, pool created lazily, warm-up,
  trim, low memory, `CollectFull`) and dense periodic *samples* (per-pool active/inactive, managed
  heap, live scope count, and per-interval deltas of the `PoolBridge` counters). Disabled by default;
  every entry point is `[Conditional]` on `UNITY_EDITOR` / `DEVELOPMENT_BUILD`, so a release build
  removes the calls and their arguments. A sampling tick allocates 0 B in steady state, asserted by a
  test — a diagnostic that produces garbage changes what it is measuring.
- **Timeline pane in the Memory Inspector**: escape-rate strip, managed-heap history, per-pool
  sparklines with a peak marker, and a recent-events list, over a shared time axis. The peak is what
  sizes a warm-up count; the instantaneous number the window showed before cannot. Gaps are drawn as
  gaps — a pool that went away must not read as a pool sitting idle at zero.

### Changed
- **The Memory Inspector is now a UI Toolkit window** (was IMGUI). Charts are stroked, anti-aliased
  polylines with a filled area, drawn via `Painter2D` in a retained element that regenerates geometry
  only when new samples arrive — the IMGUI version issued one `DrawRect` per sample per repaint, and
  repainted the entire window on every editor tick. Refresh is now scheduled at 4 Hz to match the
  recorder's sample rate. No API change; the menu item is unchanged.
- **`MemoryToolkit.Diagnostics.MemoryOverlay`**: the same data drawn on screen in a development build
  via `OnGUI` — no canvas, no prefab, no uGUI dependency — because the memory failures that matter
  happen on a low-end device, twenty minutes in, on a build nobody can attach a profiler to.
  `MemoryRecorder.Dump()` produces the equivalent as text for device logs and CI.
- **`GameObjectPool.PrefabName`**, cached at construction. `UnityEngine.Object.name` marshals a new
  managed string on every call, so reading it per pool per repaint — which the Inspector already did —
  allocated continuously. `MemoryScope.CollectStats` now uses the cached name, and the label survives
  the prefab being destroyed.

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
