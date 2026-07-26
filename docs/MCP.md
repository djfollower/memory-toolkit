# Agent access (MCP)

The toolkit's editor-side capabilities are exposed to agents over MCP: pool-safety validation, live
pool and heap state, the recorder timeline, and the mutating actions the Memory Inspector's buttons
perform.

The reason to give an agent these particular tools is that pooling adoption is a loop it can
otherwise only guess at — *is this prefab safe to pool, is it pooled now, how big should the pool be,
is anything still escaping* — and every one of those questions is answered by data that exists only
inside a running Editor. An agent without them writes a warm-up count from a call site's guess, which
is the exact mistake the package exists to remove.

## Shape

```
agent ──stdio/MCP──> Tools~/memory-toolkit-mcp/index.mjs ──HTTP/JSON-RPC──> Unity Editor
                                                            127.0.0.1, per-session token
```

The server lives inside the Editor (`Editor/Mcp/`). It has to: the validator reads deserialized
prefab data and reflects over compiled component types — a YAML parser can see neither a
`ParticleSystem`'s stop action nor an `OnDestroy` declared on a base class in another assembly — and
the stats and timeline are live process state by definition. The Node script is a translator with no
dependencies; `node index.mjs` is the whole install.

## Setup

1. In Unity: **Window > Analysis > Memory Toolkit MCP > Enable Server**. The setting is per-user and
   sticky, and the server restarts itself after every domain reload.
2. To allow the tools that change Editor state, also enable **Allow Mutating Tools** (off by
   default).
3. **Copy Client Config** puts a ready config on the clipboard. It looks like this:

```json
{
  "mcpServers": {
    "memory-toolkit": {
      "command": "node",
      "args": ["/abs/path/to/package/Tools~/memory-toolkit-mcp/index.mjs", "--project", "/abs/path/to/UnityProject"]
    }
  }
}
```

`--project` is optional. Without it the bridge picks whichever Editor most recently wrote a
heartbeat, which is what you want with one project open and not what you want with several.

Requires Node 18+.

## Tools

| Tool | Needs play mode | Answers |
|---|---|---|
| `editor_status` | no | Play mode, compiling, active scene, recorder state, whether mutations are enabled |
| `triage_project` | no | Churn, per-frame census, boot & session-boundary candidates, incumbent-pool detection → ADOPT or INTEGRATE |
| `propose_scope_map` | no | A draft Permanent / Scene / Frame map from the triage |
| `suggest_budget` | no | Recorded peaks → a draft MemoryBudget, grouped by scope |
| `explain_finding` | no | A finding → the field-guide section that says why it breaks and what to do |
| `validate_prefab` | no | Can this prefab survive being pooled? |
| `validate_project` | no | Which prefabs in this folder can't? |
| `get_pool_stats` | yes | What is pooled right now, warmed or lazy, plus gets/returns/escapes |
| `get_memory_snapshot` | yes | Heap totals, every scope's pools/arenas/pinned assets, frame arena used vs peak |
| `recorder_control` | no | Start / stop / clear the recorder |
| `get_recorder_timeline` | yes | Peak active per pool (**the warm-up count**), escape rate, events, derived findings |
| `warmup_pool` | yes | Pre-instantiate a pool, as a loading screen would |
| `trim_pools` | yes | Shed inactive instances, as `Application.lowMemory` does |
| `dispose_scope` | yes | Free a scope, as a scene unload does |
| `collect_full` | yes | Blocking GC + unused-asset sweep |

The last four change Editor state and are refused unless **Allow Mutating Tools** is on. Tools that
need play mode say so in the error rather than returning empty data — an agent that reads "enter play
mode" retries correctly, whereas an agent handed `{"pools": []}` concludes nothing is pooled.

## The loop this is for

```
triage_project             → ADOPT or INTEGRATE, with the boot entry, the session
                             boundary, and the hottest-churn prefab already located
propose_scope_map          → a draft Permanent/Scene/Frame map to react to
validate_prefab            → fix what statically disqualifies the prefab
recorder_control start     → then play, and exercise the transition (load the level, end the match)
get_recorder_timeline      → peakActive per pool is the warm-up count; escapes > 0 means
                             something still bypasses the pool
suggest_budget             → turn those peaks into a draft MemoryBudget
warmup_pool                → try the number before writing it into game code
dispose_scope              → force the scene-unload case and see what still holds instances
explain_finding            → any finding above → the guide section behind it
```

`triage_project` is the method of `docs/ADOPTION.md` §1 run as data: the six greps and the
Update/FixedUpdate census, plus incumbent-pool detection that branches to ADOPTION or INTEGRATION
automatically. It is heuristic and says so — every boot and boundary candidate carries the file and
line it was drawn from, so a wrong guess is arguable rather than authoritative, and assigning the
final lifetimes stays the human decision the guide reserves for one. Both reference codebases the
guides were written from reproduce their documented scope maps when the shipped triage is run against
them cold: the greenfield project resolves to ADOPT with its app-loader as the Permanent owner, the
brownfield one to INTEGRATE with its init-flow manager, and its ~400 pool-mentioning files detected
as the incumbent.

`explain_finding` is how a finding connects back to the method: it maps a validator issue, a timeline
anomaly, or an analyzer rule (MTK001/MTK002/MTK007) to the guide section that explains the failure and
the fix. Call it with no topic to list the slugs. The guides are referenced by section rather than
embedded, so the server stays tool-only — no separate resources channel to keep in sync.

Sizing from `peakActive` rather than from the instantaneous count is the whole point of reading the
timeline: a snapshot taken between waves shows a pool at zero. So is watching escapes — a pool that
quietly degraded into `Instantiate`/`Destroy` looks perfectly healthy in a snapshot, because the
instances it never pooled aren't there to see.

## Trust boundary

The listener binds `127.0.0.1` only, on the first free port from 8787, and requires a token generated
per Editor session. The token and port are written to `Library/MemoryToolkit/mcp.json` in the project
and mirrored to `~/.memory-toolkit/endpoints/` so the bridge can find a running Editor; both files are
deleted on shutdown, and a descriptor whose heartbeat is over two minutes stale is ignored.

Requests arrive on thread pool threads and are queued onto the main thread, since touching a Unity API
off it crashes the Editor. A request that arrives while Unity is compiling, importing, or showing a
modal dialog waits up to 30 seconds and then fails with a message saying so — the agent gets an error
it can act on instead of a hung tool call.
