# On device, and in the field

Every diagnostic so far stops at the editor, or at a pane a human watches. Memory failures do not
happen there. They happen after forty minutes on a low-end phone, to a QA tester who cannot read an
overlay and would not know what a pool escape is, or to a player whose OS kills the app and sends you
a stack trace with no memory context at all.

Two tools for that. Both compile out entirely outside the editor and development builds, like the
recorder and overlay — a memory tool must not add cost to a release build.

---

## Soak: the overnight run as an artifact

`MemorySoak` writes a session report to disk on an interval, in the **same schema the CI gate
writes** (`docs/CI.md`). An overnight QA run becomes a folder of reports a build machine parses with
the reader it already has.

```csharp
// Boot of a development build:
MemorySoak.Begin(intervalSeconds: 30f);
```

With no argument it writes to `Application.persistentDataPath/mtk-soak` — the one location writable
and pullable on every platform, because a device tester does not choose a path. Files are named by
UTC timestamp so "which is newest" is answerable from the filename alone once they are off the
device, and the runner keeps the most recent 20 by default. It also writes on pause and on quit, so
the last report is the one closest to whatever went wrong.

```csharp
MemorySoak.DumpNow();   // write one now, at a known moment (level end, return from store)
MemorySoak.Stop();      // stop the interval; keep what's written
```

The number to read in those files is **escapes**. Non-zero means instances are being destroyed
instead of pooled — a pool that quietly stopped pooling, which no snapshot shows and only a session
catches. `lazyPools` is the second: a pool created by a `Get` rather than a warm-up means a budget is
missing an entry.

For a plain-text history instead of JSON, `MemoryRecorder.DumpToFile()` writes the same report the
Inspector's Dump shows, including the shadow projection if a shadow run is active.

---

## Breadcrumbs: a crash report that explains itself

A crash reporter's custom keys ride along on the next crash. Point `MemoryBreadcrumbs` at one and
every low-memory signal refreshes them, so an OOM arrives already carrying the escape count, the live
scopes, and the busiest pools — the difference between *OOM at frame 90124* and *OOM, 41 escapes,
BattleScope still live, Projectile pool at 512/512*.

```csharp
void Awake() => MemoryBreadcrumbs.Sink = new CrashlyticsBreadcrumbSink();
```

`IBreadcrumbSink` is one method. The toolkit ships **no analytics dependency** and never will; the
Crashlytics adapter is a sample (`Samples~/DeviceSoak`) you copy and point at your reporter's real
`SetCustomKey` call.

The key set is a fixed handful — under ten keys, each well under 1 KB — on purpose. Crash reporters
cap custom keys hard (Crashlytics: 64 keys, 1 KB each), and a payload that blows the cap is silently
truncated, dropping exactly the fields you added. That is why breadcrumbs send a "busiest pools"
summary rather than a key per pool. The per-pool detail belongs in the soak file; the crash needs the
headline.

Breadcrumbs are wired to `Application.lowMemory` automatically — captured **before** the trim runs,
so the reporter records the state that triggered the warning rather than the shrunk state after it.
Call `MemoryBreadcrumbs.Capture()` directly to refresh at other moments.

---

## The `DeviceSoak` sample

`Samples~/DeviceSoak` is the whole thing wired together: a `DeviceSoakBootstrapper` you drop on a
boot object that sets the breadcrumb sink, starts the soak, and optionally shows the overlay for a
tester holding the device. Copy it, replace one commented line with your crash reporter's call, and a
development build is instrumented end to end.
