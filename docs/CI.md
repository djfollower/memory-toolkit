# The build-farm gate

Everything else in this package is used by a person in an Editor session. This is the part a build
machine runs, unattended, on every change — which is what keeps a migration from quietly rotting six
months after the engineer who did it moved to another team.

Two gates, deliberately separate.

| | Needs | Runs |
|---|---|---|
| **Static** | Nothing. Asset data only. | Every build. Always on. |
| **Dynamic** | An automated play session the project supplies. | Nightly, or on the soak build. |

The static gate is the one that ships first, and it is not a consolation prize: most of the failures
in [`ADOPTION.md`](ADOPTION.md) §4 are visible in prefab data before anyone plays. A team with no
automated play sessions still gets value on day one — which decides whether any of this gets wired up
at all.

---

## Static gate

```bash
Unity -batchmode -quit -projectPath . \
      -executeMethod MemoryToolkit.Editor.CI.MemoryToolkitCI.Validate \
      -mtk-out memory-report.json \
      -mtk-junit memory-report.xml \
      -mtk-fail-on error
```

No graphics device, no play mode, no scene. Every argument is optional: with none, it validates every
prefab under `Assets` and fails on errors.

| Argument | Default | |
|---|---|---|
| `-mtk-folder` | `Assets` | Folder to sweep. A folder that does not exist is an error, not an empty result. |
| `-mtk-budget` | none | Path to a `MemoryBudget` asset to audit. A path that resolves to nothing is an error. |
| `-mtk-out` | none | JSON report path. Directories are created. |
| `-mtk-junit` | none | JUnit XML path, for the CI UI's test tab. |
| `-mtk-min-severity` | `Warning` | Issues less severe than this are counted but not reported. |
| `-mtk-fail-on` | `Error` | `Never` \| `Error` \| `Warning`. |
| `-mtk-max-prefabs` | 5000 | Ceiling on prefabs loaded. Hitting it is reported — the result is then a floor. |

**Exit codes:** `0` clean · `1` findings at or above `-mtk-fail-on` · `2` the gate itself failed.

That third code matters. `-quit` on its own exits 0 no matter what happened, so a gate that crashes
would look exactly like a gate that passed; `Validate` sets the code explicitly.

### Start at `-mtk-fail-on Never`

The first run on a real project finds a backlog. Failing the build on day one gets the gate switched
off, permanently, by someone with a release to ship. Run it as report-only, agree the backlog, then
move to `Error`, and to `Warning` once the count is at zero.

### What it checks

1. **Every prefab**, through `PoolSafetyValidator` — Stop Action = Destroy including child systems,
   `OnDestroy` doing cleanup, rigidbodies with no `IPoolable` reset, missing scripts.
2. **The budget asset**, if one is given, through `MemoryBudgetAudit`:
   - a warm-up larger than its max size on any tier — the pool would destroy what it just created,
     on the loading screen, silently
   - a direct prefab reference in a non-Permanent scope, which pins that prefab and its dependencies
     for the whole session (see [`BUDGETS.md`](BUDGETS.md))
   - an entry referencing neither a prefab nor a key, a duplicate scope entry (only the first is ever
     applied), a duplicate prefab in one scope
   - an entry with no warm-up on any tier — it does nothing
   - inverted tiers: Low warming more than High
   - **a budgeted prefab that fails pool safety** — the check that connects the budget to the thing
     it configures

Both share one sweep (`PoolProjectScan`) with the MCP `validate_project` tool, so the agent and the
build machine can never disagree about whether the project is clean.

---

## Dynamic gate

The static gate cannot see the failure that matters most: a pool that has quietly stopped pooling.
That is a property of a session, so it needs one.

Call this from the project's own automated play session, after it ends:

```csharp
bool passed = MemoryToolkitCI.WriteSessionReport("memory-session.json", budget);
```

It writes escapes, gets, returns, lazily-created pools, managed heap against the budget's ceiling,
and per-pool active/inactive/warmed. It returns false when escapes are non-zero or the heap ceiling
is exceeded.

**Escapes is the number.** Instances that reached `PoolBridge.Return` owned by no toolkit pool, and
so were destroyed rather than pooled. Non-zero means pooling is not working and is costing more than
not pooling at all. Captured before a migration it is the baseline
([`INTEGRATION.md`](INTEGRATION.md) §1); captured nightly afterwards it is the regression test, and
it is the only one that catches a registry that a scene load started wiping again.

`lazyPools` is the second number to watch: a pool created by a `Get` rather than by a warm-up paid an
`Instantiate` during gameplay and took its capacity from whichever call site happened to run first.
In a project with a budget, a non-zero count means the budget is missing an entry.

---

## GitHub Actions

```yaml
- name: Memory Toolkit gate
  run: |
    "$UNITY" -batchmode -quit -projectPath . \
      -executeMethod MemoryToolkit.Editor.CI.MemoryToolkitCI.Validate \
      -mtk-budget Assets/Settings/MemoryBudget.asset \
      -mtk-out artifacts/memory-report.json \
      -mtk-junit artifacts/memory-report.xml \
      -mtk-fail-on error

- uses: actions/upload-artifact@v4
  if: always()
  with:
    name: memory-report
    path: artifacts/
```

`if: always()` matters — the report is most worth reading on the run that failed.

## Jenkins

```groovy
stage('Memory gate') {
    steps {
        sh """${UNITY} -batchmode -quit -projectPath . \
             -executeMethod MemoryToolkit.Editor.CI.MemoryToolkitCI.Validate \
             -mtk-junit memory-report.xml -mtk-out memory-report.json \
             -mtk-fail-on error"""
    }
    post {
        always {
            junit 'memory-report.xml'
            archiveArtifacts 'memory-report.json'
        }
    }
}
```

In the JUnit output, errors are failures and warnings are `skipped` — visible in the CI UI without
turning the build red before the team has agreed to that.

---

## Report schema

Both reports carry `schemaVersion` (currently `1`) and a `kind` of `static` or `session`. Check it;
the shape will grow.

```json
{
  "schemaVersion": 1,
  "kind": "static",
  "folder": "Assets",
  "budget": "MemoryBudget",
  "prefabsFound": 412, "prefabsScanned": 412, "prefabsWithFindings": 6,
  "hitPrefabCap": false,
  "totalErrors": 2, "totalWarnings": 11,
  "findings": [
    { "severity": "Error", "path": "Assets/FX/Explosion.prefab: Explosion/Sparks",
      "message": "ParticleSystem Stop Action is Destroy..." }
  ]
}
```
