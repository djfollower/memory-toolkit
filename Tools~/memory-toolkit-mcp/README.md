# memory-toolkit-mcp

MCP stdio bridge to the Memory Toolkit server running inside the Unity Editor. Dependency-free; Node
18+.

```bash
node index.mjs [--project /path/to/UnityProject] [--endpoint /path/to/mcp.json]
```

Without `--project`, the bridge connects to whichever Editor most recently wrote a heartbeat to
`~/.memory-toolkit/endpoints/`. `MEMORY_TOOLKIT_PROJECT` works in place of `--project`.

The Unity side must be enabled: **Window > Analysis > Memory Toolkit MCP > Enable Server**. Tools that
change Editor state additionally need **Allow Mutating Tools**.

The tool list is served by Unity, so it never drifts from the C# implementation. When the Editor is
closed the bridge serves its last known list from `~/.memory-toolkit/tools-cache.json` and announces
`notifications/tools/list_changed` once the Editor comes back — a client reads the tool list once, at
connect, and a session that started while Unity was closed would otherwise stay empty forever.

See `docs/MCP.md` in the package for the tool reference.
