# The analyzer

The field guides describe call-site failures in prose. Prose does not survive contact with a
thirty-person team. A rule with an ID does: it can be suppressed on the one line that is genuinely an
exception, tracked in review, and argued about — a paragraph in `ADOPTION.md` cannot.

The analyzer ships as a Roslyn DLL that Unity loads automatically (the `RoslynAnalyzer` asset label,
already set). Nothing to install, no package manifest change. It reports in the IDE and in the
Console, at compile time — enforcement at the moment of writing rather than at review.

---

## The rules

| ID | What | Default |
|---|---|---|
| **MTK001** | `?.`, `??`, `??=`, `is null` on a `UnityEngine.Object` | **Warning (on)** |
| **MTK002** | Allocation in `Update` / `LateUpdate` / `FixedUpdate` | **Warning (on)** |
| **MTK007** | `UnityEngine.Object` used after an `await` without a re-check | Off |

Two are on by default; one is off. The split is not caution for its own sake — it is the empirical
result below.

### MTK001 — null-check that bypasses Unity's lifetime check

A destroyed `UnityEngine.Object` is not reference-null. Unity overloads `==` so it *reports* as null
while the managed wrapper is still a live reference. `?.`, `??`, `??=` and `is null` all compile
straight to reference comparisons, so each one sails past a destroyed object and fails somewhere
else, far from the cause. This is the single most common bug at a pool boundary, and invisible in
review because it is correct C# for every non-Unity type in the same file.

```csharp
if (target != null) target.Play();   // correct
target?.Play();                       // MTK001 — runs on a destroyed target
```

On by default because it is decidable from type information alone: if the expression derives from
`UnityEngine.Object`, the check is wrong, no project knowledge required.

### MTK002 — allocation in a per-frame method

`Update`, `LateUpdate` and `FixedUpdate` run every frame, so an allocation here is garbage at frame
rate — the difference between "low GC" and 0 B/frame, and invisible in review, obvious in the
Profiler's GC Alloc column.

Scoped narrowly on purpose. It flags only the shapes that are unconditionally per-frame garbage and
that this package has a specific answer for: collections and arrays (`ListPool`, `ArrayPool`, the
frame arena), string building and interpolation (`StringBuilderCache`), LINQ (a closure and an
enumerator per call), and Unity's yield instructions (cache one — they are immutable). It does **not**
flag `new Vector3(...)` or any other struct, and it only fires on a `MonoBehaviour` — a service with
a method called `Update` does not run every frame.

### MTK007 — use after await

Pooling breaks the rule that a non-null reference is still yours. Across an `await` the target may
have been destroyed, or released and re-taken — alive, non-null, and someone else's. A null check
passes; `PooledRef<T>` is what answers the question.

Off by default, and honestly so: whether an await can outlive its target is project knowledge this
analyzer does not have. A one-frame yield in a system nothing can tear down is fine, and the rule
cannot tell that apart from an Addressables load across a level transition. Enable it per-folder once
you have seen your own noise level.

---

## Why the split is what it is

The ship gate for these rules was a measured false-positive rate against two real production
codebases — the same two the field guides are built on — not a judgement call.

| | Files | MTK001 | MTK002 | MTK007 |
|---|---|---|---|---|
| Greenfield merge/puzzle game | 1,867 | 16 | 4 | 42 |
| Brownfield card game | 5,874 | 761 | 7 | 818 |

Every MTK001 and MTK002 finding reviewed was a true positive — including, in the greenfield project,
one file that guards `_timeline` with `if (_timeline != null)` on one line and calls `_timeline?.Play()`
on another. That is the whole case for the rule: the correct form and the broken form sitting in the
same file, indistinguishable to a reviewer.

MTK007's count is why it is off. At ~42 and ~818 findings it is not measuring a bug rate, it is
measuring how often code touches a Unity object after an await — most of which is fine. Shipping it on
would get the whole analyzer switched off within a day, taking MTK001 and MTK002 with it. Off by
default, it is there for the teams who choose to scope it.

---

## Turning rules on and off

Standard `.editorconfig`, which Unity respects:

```ini
[*.cs]
# Enable the opt-in rule, scoped by putting this in a folder's .editorconfig
dotnet_diagnostic.MTK007.severity = warning

# Promote a rule to an error once the backlog is clear
dotnet_diagnostic.MTK001.severity = error
```

Suppress a genuine exception on the line:

```csharp
#pragma warning disable MTK001 // this reference is a plain C# type via an interface, not a Unity object
```

Suppress it for a whole third-party folder with a scoped `.editorconfig` setting the severity to
`none` — worth doing for imported plugins you do not control, which is where most of the noise in any
analyzer originates.

---

## Building it

The source is in `Analyzers~/` (the trailing tilde keeps Unity from importing the C# project as
game code). The shipped artifact is `Analyzers/MemoryToolkit.Analyzers.dll`.

```bash
cd Analyzers~/MemoryToolkit.Analyzers && dotnet build -c Release
cp bin/Release/netstandard2.0/MemoryToolkit.Analyzers.dll ../../Analyzers/
```

`netstandard2.0` and Roslyn 3.8 are deliberate: Unity loads analyzers into its own Roslyn host, and
building against a newer compiler than the host provides is the usual cause of an analyzer that is
silently ignored. Tests live in `Analyzers~/MemoryToolkit.Analyzers.Tests` (`dotnet test`) and run
against a stub UnityEngine, so they need no Unity install — but the acceptance measure is the
real-project scan above, not the unit tests.
