# Roadmap: fitting Memory Toolkit into a studio workflow

The API is not the problem. Every entry point today is **a human in an interactive Editor
session** — a menu item to validate, a window to read peaks from, a Record button, an MCP server
someone toggles on. A studio's workflow is a build farm, QA on devices, gameplay programmers who
will not read `docs/ADOPTION.md`, and a tech lead who needs a gate.

Nothing in the package currently *produces or consumes an artifact*, so it cannot participate in any
of that.

**Through-line for every workstream below: turn a human-in-the-editor step into a file a pipeline
can read or write.**

---

## Sequencing

| Phase | Version | Workstream | Why here |
|---|---|---|---|
| 0 | 0.9.0 ✅ | **A** — Shadow mode | No dependencies, smallest, and it is what gets the rest funded. Produces the number that justifies a migration sprint before anyone changes a call site. |
| 1 | 0.10.0 ✅ | **B** — Budgets as data → **C** — Headless gate | B defines the schema C asserts against. Build as one unit; separately they cost more. |
| 2 | 0.11.0 ✅ | **D** — DI / lifecycle adapters | Decides whether adoption is a one-line installer change or an architecture argument. Needs nothing from 0–1, but is worth less before budgets exist (the installer line is `ApplyBudget`). |
| 3 | 0.12.0 ✅ | **E** — Roslyn analyzer | Independent but expensive, and the one most likely to be switched off if rushed. Wants the reference projects free for false-positive measurement. |
| 4 | 0.13.0 ✅ | **F** — Device, soak, field telemetry | Reuses C's JSON reader and B's ceilings. Real memory bugs are field bugs, so this is where the toolkit stops being an Editor tool. |
| 5 | 1.0.0 ✅ | **G** — Agent triage over MCP | Last deliberately: it should encode the *stabilized* budget + validator surfaces, not chase them. |

The cut line, if this has to be time-boxed: **A, B, C, D ship a product.** E, F, G are what make it
better than every other pooling package.

---

## WS-A — Shadow mode (Phase 0)

**Goal.** Measure what pooling *would* save, changing no behavior, before any migration lands.

`PoolBridge` already tracks `UnknownInstanceCount`, `GetCount`, `ReturnCount`, `LazyPoolCount`
(`Runtime/Migration/PoolBridge.cs`). Extend it into an observe-only mode and it becomes the universal
adoption seam — not just the brownfield one. A greenfield project routes its `Instantiate`/`Destroy`
through `PoolBridge` in Observe mode and gets the projection with zero risk.

**Deliverables**
- `PoolBridge.Mode` — `Active` (today) | `Observe`. In Observe, `Get` calls `Object.Instantiate` and
  `Return` calls `Object.Destroy`, touching no pool.
- `Runtime/Migration/PoolShadow.cs` — per-prefab counters: gets, returns, **peak concurrent live**,
  estimated churn. Peak concurrent is the number that sizes a pool; nothing else in the package can
  produce it pre-migration.
- `PoolShadow.Report()` → text and JSON: per prefab, "N instantiates avoided, warm-up would be P".
- New `MemoryEventKind` entries so shadow data lands in the existing Timeline pane.

**Acceptance**
- Run a full session on both reference projects; produce the projected-savings table.
- Test: in Observe mode, `Get` never touches a `GameObjectPool` and pool counts stay zero.

**Risks**
- Peak-concurrent tracking needs a live-instance set, which allocates. Gate the whole shadow path
  behind `DEVELOPMENT_BUILD || UNITY_EDITOR` like `MemoryRecorder` and `MemoryOverlay` already are.
- Observe mode must be impossible to ship on by accident — log loudly on enable, and assert it is off
  in release builds.

**Size:** S (2–3 days)

---

## WS-B — Budgets as data (Phase 1)

**Goal.** Warm-up counts, `maxSize`, arena capacities and heap ceilings stop being C# literals in
installers and become an asset that a tech artist or QA can tune, **per device tier**.

A single warm-up number is wrong on two of your three targets. That is the most likely reason a tool
like this gets partially reverted six months in.

**Deliverables**
- `Runtime/Budgets/MemoryBudget.cs` — ScriptableObject:
  - scope entries: scope name → `{ prefab, warmup, maxSize }[]`
  - arena entries: scope name → capacity bytes
  - ceilings: managed heap, frame-scratch bytes
- `MemoryBudgetTier` + `IDeviceTierProvider`, default implementation over `SystemInfo.systemMemorySize`
  and platform. Per-tier overrides on every numeric field.
- `MemoryScope.ApplyBudget(MemoryBudget budget, MemoryBudgetTier tier)` — one call replaces a
  hand-written installer. This is what makes ADOPTION Steps 1–2 a data change.
- Editor: budget inspector with **"Apply measured peaks"**, reading `PeakActive` off
  `MemoryRecorder.PoolSeriesList`. Today that transcription is a manual step the docs merely instruct
  (`docs/ADOPTION.md` §6, last checkbox) — this closes the measurement→config loop.
- `MemoryManager.FrameScratchCapacityBytes` and `LowMemoryKeepPerPool` become budget-sourced, with
  the current fields as fallback.

**Acceptance**
- `Samples~/SceneScopeLevel` converted to budget-driven; the installer loses its hardcoded numbers.
- Round-trip test: record a session → apply peaks → assert the asset holds the recorded values.
- Tier test: same budget resolves to different warm-ups for two synthetic device profiles.

**Risks**
- **Footgun to design around up front:** a budget asset holding direct `GameObject` references pins
  every listed prefab in memory the moment the budget loads — a memory tool that costs memory.
  Direct references only for the Permanent tier; everything else keys by `AssetReference` /
  addressable key and resolves lazily.
- `PeakActive` and `PoolSeriesList` are `internal`. `Editor/AssemblyInfo.cs` already has
  `InternalsVisibleTo`, so the Editor path is fine, but the CI path (WS-C) needs a considered public
  read API rather than widening `internal` ad hoc.

**Size:** M (1–1.5 weeks)

---

## WS-C — Headless gate (Phase 1)

**Goal.** `-batchmode -executeMethod` → JSON + JUnit XML → non-zero exit. The nightly build stops the
migration from silently rotting.

**Deliverables**
- `Editor/CI/MemoryToolkitCI.cs` — entry point; args `-mtk-budget`, `-mtk-out`, `-mtk-junit`,
  `-mtk-fail-on`.
- **Refactor first:** the project-wide prefab scan currently lives inside the `validate_project` MCP
  handler in `Editor/Mcp/McpTools.cs`. Extract it to `Editor/PoolProjectScan.cs` so the menu item,
  MCP, and CI share exactly one implementation. Three copies of this check will diverge.
- Two gates, deliberately separated:
  - **Static gate (always on, universal):** `PoolSafetyValidator` across all prefabs. Asset-only, so
    it runs in batchmode with no graphics device and needs nothing from the studio.
  - **Dynamic gate (opt-in):** over a play session the studio supplies, assert budget ceilings and
    **`escapes == 0`**. Escapes is already documented as "the number to drive to zero" and as the
    pre-migration regression baseline (`docs/INTEGRATION.md` §7) — it is the best regression metric
    in the package and today only a human looking at a pane can see it.
- `docs/CI.md` with working GitHub Actions and Jenkins snippets.

**Acceptance**
- Fixture prefab with `stopAction: 2` fails the build in batchmode with the right exit code.
- Clean project exits 0; JUnit XML parses in both CI systems.

**Risks**
- Do not make the dynamic gate mandatory — a studio without automated play sessions must still get
  value on day one, or they will not wire up any of it.
- Version the JSON schema from the first release. WS-F reuses this reader for device dumps.

**Size:** M (1 week)

---

## WS-D — DI and lifecycle adapters (Phase 2)

**Goal.** Stop asking studios to adopt a second lifetime model.

Most Unity studios already express lifetime through VContainer `LifetimeScope` or a Zenject context.
A `MemoryScope` alongside that is a second ownership system that can disagree with the first — and
the disagreement will be blamed on this package.

**Deliverables**
- `Runtime/Integrations/VContainer/` and `.../Zenject/`, each its own asmdef with `versionDefines`,
  no hard dependency — the exact pattern `Runtime/Addressables/MemoryToolkit.Addressables.asmdef`
  already establishes.
- VContainer: `builder.RegisterMemoryScope(name)`; the scope is disposed with the container.
- Zenject: `MemoryScopeInstaller` binding a scope to the context's lifetime.
- Scene-loader independence: `CreateSceneScope()` hooks Unity's own scene unload, which misses
  Addressables scene loads and bespoke flow managers. Add an explicit
  `MemoryScope.AttachTo(...)` for a handle, GameObject, or arbitrary disposable trigger.
- One sample per container, mirroring `Samples~/SceneScopeLevel`.

**Acceptance**
- Container disposal disposes the scope, in the right order, with the LIFO guarantee intact
  (`docs/ADOPTION.md` §5.4).
- Compile matrix project per container.

**Risks**
- ~~CI cannot verify these without the third-party packages installed.~~ **Resolved during
  implementation:** the packages install into a throwaway project, so `Tests/Integrations/` holds a
  test assembly per container gated on the same version define. It compiles away for projects with
  neither, and the suite verifies against VContainer 1.16.5 and Extenject 9.2.0 for real. No
  compile-matrix project needed.

**Size:** M (1 week, plus per-container samples)

---

## WS-E — Roslyn analyzer (Phase 3)

**Goal.** Move the rules from prose to squiggles. `docs/INTEGRATION.md` §7 already flags this as the
open gap; it is larger than the three patterns listed there. "No allocation in per-frame code" does
not survive contact with a 30-person team as documentation.

**Deliverables**
- `Analyzers~/MemoryToolkit.Analyzers` — netstandard2.0, shipped as a DLL with Unity's `RoslynAnalyzer`
  asset label (the supported mechanism; no package manifest changes needed).
- Rules, with IDs so they can be suppressed per line and tracked in review:
  - `MTK001` — `?.` / `??` applied to a `UnityEngine.Object` at a pool boundary (failure D)
  - `MTK002` — allocation inside `Update`/`FixedUpdate`/`LateUpdate` (array/list `new`, LINQ, string
    interpolation, closure capture)
  - `MTK003` — `Instantiate`/`Destroy` on a type that is pooled elsewhere
  - `MTK004` — release path that skips reparenting (failure B)
  - `MTK005` — pool key derived from a loaded asset's runtime identity (failure E)
  - `MTK006` — `AddComponent` in the spawn path of a pooled prefab
  - `MTK007` — no scope/instance re-check after an `await` in gameplay code
- Ship `MTK001`/`MTK002` as warnings; everything else **off by default**, opt-in via `.editorconfig`.

**Acceptance**
- Run against both reference projects. **Ship only the rules that measure under ~5% false positives
  there.** A rule that cries wolf gets the whole analyzer disabled, which costs more than not
  shipping it — this is the stated reason it has not been built yet, and the gate has to be real.

**Risks**
- Precision, as already identified. Mitigate by scoping the call-site rules to files that already
  reference the pool, rather than the whole project.

**Size:** L (2–3 weeks, dominated by false-positive tuning)

---

## WS-F — Device, soak, and field (Phase 4)

**Goal.** `MemoryOverlay` and `Dump()` stop at "look at the screen." Memory failures happen after 40
minutes on a low-end Android, to a QA tester who cannot read a pane.

**Deliverables**
- `MemoryRecorder.DumpToFile(path)` + a `SoakRunner` component: interval dumps, rotation, JSON in
  WS-C's schema, so an overnight QA run becomes a CI artifact parsed by code that already exists.
- `Runtime/Diagnostics/MemoryBreadcrumbs.cs` — an `IBreadcrumbSink` interface with a no-op default.
  Keys: live scope names, per-pool totals, escape count, peak managed bytes, last low-memory time.
- A Crashlytics sink as a **sample**, not a dependency. An OOM then arrives with a memory postmortem
  attached instead of a stack trace with no context.

**Acceptance**
- 40-minute soak on a real low-end Android device; artifact pulled and parsed by the WS-C reader.
- A forced OOM in a dev build carries readable breadcrumbs into the crash report.

**Risks**
- No hard analytics dependency, ever — sink interface only.
- Respect sink payload limits (Crashlytics: 64 custom keys, 1 KB each). Budget the key set at design
  time rather than truncating at runtime.

**Size:** M (1–1.5 weeks)

---

## WS-G — Agent triage over MCP (Phase 5)

**Goal.** The differentiator. The real asset in this repo is the *method* in the two field guides;
the MCP server (`Editor/Mcp/`) already exists to expose it. Turn triage from something a person reads
into something an agent executes.

**Deliverables**
- `triage_project` — the six greps of `docs/ADOPTION.md` §1, the `Update`/`FixedUpdate` census, and
  incumbent-pool detection; branches to the ADOPTION or INTEGRATION path automatically (grep 4).
- `propose_scope_map` — draft of the §2 table: boot entry point → Permanent, session teardown →
  Scene, per-frame query sites → Frame.
- `suggest_budget` — recorder peaks → a draft `MemoryBudget` asset (WS-B).
- `explain_finding` — validator issue or timeline anomaly → the relevant field-guide section.
- Ship both guides as MCP resources so the agent's method *is* the documented method.

**Acceptance**
- Run cold against both reference projects and diff the output against the hand-written scope maps
  already in the guides. That ground truth exists — use it as the test.

**Risks**
- Grep-based triage is heuristic. Have it emit confidence and cite the files it drew from, so a wrong
  scope map is arguable rather than authoritative.

**Size:** M (1–1.5 weeks)

---

## Cross-cutting

**Docs.** `README.md` is already carrying more than a README should. Phase 1 should split out
`docs/BUDGETS.md` and `docs/CI.md`, and reduce the README to the tour plus links.

**Versioning.** Semver per the phase table; `package.json` is at 0.8.0. 1.0.0 lands with WS-G, at
which point the package covers measure → configure → enforce → observe in the field.

**Testing.** WS-A/B/C are fully unit-testable and should hold the current bar. WS-D and WS-F cannot
be — say so in `CHANGELOG.md` rather than implying coverage that does not exist.

**The reference projects stay the gate.** Every workstream's acceptance criterion above runs against
the two real codebases, not a synthetic scene. That is what has made the existing guides credible and
it is the thing most easily lost once the work turns into infrastructure.
