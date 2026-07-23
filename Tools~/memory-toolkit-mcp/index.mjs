#!/usr/bin/env node
// Memory Toolkit — MCP stdio bridge.
//
// The tools live inside the Unity Editor (see Editor/Mcp/McpTools.cs): pool-safety
// validation needs deserialized prefab data and compiled type metadata, and the pool
// stats and recorder timeline are live process state. This process only translates —
// MCP over stdio on one side, the Editor's loopback JSON-RPC endpoint on the other —
// and it is deliberately dependency-free so `node index.mjs` is the whole install.
//
// The Editor is not always there. It restarts, reloads its app domain on every script
// change, and is simply closed most of the time. So nothing here is bound at startup:
// the endpoint is re-read per request, and the tool list is served from a cache when
// Unity is down, with a tools/list_changed notification once it comes back.

import { readFileSync, writeFileSync, readdirSync, mkdirSync, existsSync } from "node:fs";
import { request } from "node:http";
import { homedir } from "node:os";
import { dirname, join } from "node:path";

const PROTOCOL_VERSION = "2025-06-18";
const SUPPORTED_PROTOCOLS = new Set(["2024-11-05", "2025-03-26", "2025-06-18"]);
const REGISTRY_DIR = join(homedir(), ".memory-toolkit", "endpoints");
const CACHE_FILE = join(homedir(), ".memory-toolkit", "tools-cache.json");

// The Editor rewrites its descriptor every 30s; anything older is a file left
// behind by an Editor that was killed rather than closed.
const HEARTBEAT_STALE_MS = 120_000;
const REQUEST_TIMEOUT_MS = 60_000;
const POLL_INTERVAL_MS = 10_000;

const options = parseArgs(process.argv.slice(2));

const OFFLINE_HINT =
  "Unity Editor not reachable. Open the project, then enable " +
  "Window > Analysis > Memory Toolkit MCP > Enable Server.";

let toolsCache = loadToolsCache();
let lastToolNames = toolsCache.map((tool) => tool.name).join(",");
let initialized = false;
let pollTimer = null;

// ---- Endpoint discovery -------------------------------------------------------

function parseArgs(argv) {
  const parsed = { project: process.env.MEMORY_TOOLKIT_PROJECT || null, endpoint: null };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--project" && argv[i + 1]) parsed.project = argv[++i];
    else if (argv[i] === "--endpoint" && argv[i + 1]) parsed.endpoint = argv[++i];
  }
  return parsed;
}

function readDescriptor(path) {
  try {
    const descriptor = JSON.parse(readFileSync(path, "utf8"));
    if (!descriptor.port || !descriptor.token) return null;
    return descriptor;
  } catch {
    return null;
  }
}

/**
 * Resolves the live Editor endpoint, in order of how explicit the instruction was:
 * an endpoint file, a named project, then whichever registered Editor is still
 * beating. Called per request — a domain reload or an Editor restart changes both
 * the port and the token, and binding either at startup would wedge the session.
 */
function resolveEndpoint() {
  if (options.endpoint) return readDescriptor(options.endpoint);

  if (options.project) {
    return readDescriptor(join(options.project, "Library", "MemoryToolkit", "mcp.json"));
  }

  if (!existsSync(REGISTRY_DIR)) return null;

  const live = [];
  for (const entry of readdirSync(REGISTRY_DIR)) {
    if (!entry.endsWith(".json")) continue;
    const descriptor = readDescriptor(join(REGISTRY_DIR, entry));
    if (!descriptor) continue;
    const age = Date.now() - (descriptor.updatedAt ?? 0) * 1000;
    if (age > HEARTBEAT_STALE_MS) continue;
    live.push(descriptor);
  }

  if (live.length === 0) return null;
  live.sort((a, b) => (b.updatedAt ?? 0) - (a.updatedAt ?? 0));
  return live[0];
}

function callUnity(method, params) {
  const descriptor = resolveEndpoint();
  if (!descriptor) return Promise.reject(new Error(OFFLINE_HINT));

  const body = JSON.stringify({ jsonrpc: "2.0", id: 1, method, params });

  return new Promise((resolve, reject) => {
    const req = request(
      {
        host: "127.0.0.1",
        port: descriptor.port,
        path: "/",
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Content-Length": Buffer.byteLength(body),
          "X-MemoryToolkit-Token": descriptor.token,
        },
        timeout: REQUEST_TIMEOUT_MS,
      },
      (res) => {
        let text = "";
        res.setEncoding("utf8");
        res.on("data", (chunk) => (text += chunk));
        res.on("end", () => {
          let payload;
          try {
            payload = JSON.parse(text);
          } catch {
            reject(new Error(`Malformed response from the Editor (HTTP ${res.statusCode}).`));
            return;
          }
          if (payload.error) reject(new Error(payload.error.message || "Editor returned an error."));
          else resolve(payload.result);
        });
      },
    );

    req.on("timeout", () => req.destroy(new Error("The Editor did not respond in time.")));
    req.on("error", (error) =>
      reject(new Error(error.code === "ECONNREFUSED" ? OFFLINE_HINT : error.message)),
    );
    req.end(body);
  });
}

// ---- Tool list ----------------------------------------------------------------

function loadToolsCache() {
  try {
    return JSON.parse(readFileSync(CACHE_FILE, "utf8")).tools ?? [];
  } catch {
    return [];
  }
}

function saveToolsCache(tools) {
  try {
    mkdirSync(dirname(CACHE_FILE), { recursive: true });
    writeFileSync(CACHE_FILE, JSON.stringify({ tools }, null, 2));
  } catch {
    // A cache that cannot be written only costs an empty tool list next time the
    // Editor happens to be closed at connect.
  }
}

async function fetchTools() {
  const result = await callUnity("tools/list", {});
  const tools = result?.tools ?? [];
  if (tools.length > 0) {
    toolsCache = tools;
    saveToolsCache(tools);
  }
  return tools;
}

/**
 * Clients read the tool list once, at connect. If the Editor was closed then, the
 * session would be permanently empty — so poll, and announce the list when it
 * appears or changes.
 */
function startPolling() {
  if (pollTimer) return;
  pollTimer = setInterval(async () => {
    try {
      const tools = await fetchTools();
      const names = tools.map((tool) => tool.name).join(",");
      if (names && names !== lastToolNames) {
        lastToolNames = names;
        send({ jsonrpc: "2.0", method: "notifications/tools/list_changed" });
      }
    } catch {
      // Editor closed or reloading; the next tick tries again.
    }
  }, POLL_INTERVAL_MS);
  pollTimer.unref?.();
}

// ---- MCP over stdio -----------------------------------------------------------

function send(message) {
  process.stdout.write(JSON.stringify(message) + "\n");
}

function reply(id, result) {
  send({ jsonrpc: "2.0", id, result });
}

function replyError(id, code, message) {
  send({ jsonrpc: "2.0", id, error: { code, message } });
}

function textResult(value, isError = false) {
  return {
    content: [{ type: "text", text: typeof value === "string" ? value : JSON.stringify(value, null, 2) }],
    ...(typeof value === "object" && value !== null ? { structuredContent: value } : {}),
    isError,
  };
}

async function handle(message) {
  const { id, method, params } = message;
  const isNotification = id === undefined || id === null;

  switch (method) {
    case "initialize": {
      initialized = true;
      startPolling();
      const requested = params?.protocolVersion;
      reply(id, {
        protocolVersion: SUPPORTED_PROTOCOLS.has(requested) ? requested : PROTOCOL_VERSION,
        capabilities: { tools: { listChanged: true } },
        serverInfo: { name: "memory-toolkit", version: "0.8.0" },
        instructions:
          "Tools run inside a Unity Editor with the Memory Toolkit package installed. Validation works any " +
          "time the Editor is open; live pool, heap, and recorder tools need play mode. Call editor_status " +
          "first if a tool reports the wrong state.",
      });
      return;
    }

    case "notifications/initialized":
      return;

    case "ping":
      if (!isNotification) reply(id, {});
      return;

    case "tools/list": {
      try {
        const tools = await fetchTools();
        lastToolNames = tools.map((tool) => tool.name).join(",");
        reply(id, { tools });
      } catch (error) {
        // Serve the cached list rather than nothing: the agent can still see what
        // exists, and calling a tool returns the same actionable message.
        reply(id, { tools: toolsCache, _warning: `${error.message}` });
      }
      return;
    }

    case "tools/call": {
      const name = params?.name;
      try {
        const result = await callUnity("tools/call", { name, arguments: params?.arguments ?? {} });
        reply(id, textResult(result));
      } catch (error) {
        // A failed tool call is reported as a result, not a protocol error: the
        // model should read "enter play mode" and act, not see the call vanish.
        reply(id, textResult(`${name} failed: ${error.message}`, true));
      }
      return;
    }

    default:
      if (!isNotification) replyError(id, -32601, `Unknown method '${method}'.`);
  }
}

let buffer = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", (chunk) => {
  buffer += chunk;
  let newline;
  while ((newline = buffer.indexOf("\n")) >= 0) {
    const line = buffer.slice(0, newline).trim();
    buffer = buffer.slice(newline + 1);
    if (!line) continue;

    let message;
    try {
      message = JSON.parse(line);
    } catch {
      replyError(null, -32700, "Parse error.");
      continue;
    }

    handle(message).catch((error) => {
      if (message.id !== undefined && message.id !== null) {
        replyError(message.id, -32603, error.message);
      }
    });
  }
});

process.stdin.on("end", () => process.exit(0));
process.on("SIGTERM", () => process.exit(0));

// stderr is the only channel that cannot corrupt the protocol stream.
if (!initialized) process.stderr.write("[memory-toolkit-mcp] ready\n");
