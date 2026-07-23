using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace MemoryToolkit.Editor.Mcp
{
    /// <summary>
    /// Exposes the toolkit's editor-side capabilities — pool-safety validation,
    /// live pool and heap state, the recorder timeline, and the mutating actions
    /// the Memory Inspector's buttons perform — to an agent, over a loopback
    /// JSON-RPC endpoint that <c>Tools~/memory-toolkit-mcp</c> bridges to MCP.
    ///
    /// <para><b>Why in the Editor rather than a standalone analyzer.</b> Every
    /// question worth asking needs something only a running Editor has. The
    /// validator reads deserialized prefab data and reflects over compiled
    /// component types — a YAML parser can see neither a <c>ParticleSystem</c>'s
    /// stop action through its serialized property soup nor an <c>OnDestroy</c>
    /// declared on a base class in another assembly. The stats and the timeline
    /// are, by definition, live process state. An agent asking "is this prefab
    /// safe to pool, and how big should the pool be" is asking two questions that
    /// only exist inside Unity.</para>
    ///
    /// <para><b>Threading.</b> <see cref="HttpListener"/> hands requests to thread
    /// pool threads; touching a Unity API off the main thread crashes the Editor.
    /// Every request is therefore queued onto <see cref="EditorApplication.update"/>
    /// and awaited with a timeout, so a request that arrives mid-compile fails with
    /// an error the agent can read instead of hanging its tool call.</para>
    ///
    /// <para><b>Trust boundary.</b> Loopback only, plus a per-session token written
    /// to a file only this user can read. The mutating tools can dispose a scope
    /// and force a GC in the running Editor; they are off unless enabled.</para>
    /// </summary>
    [InitializeOnLoad]
    internal static class McpServer
    {
        private const string EnabledKey = "MemoryToolkit.Mcp.Enabled";
        private const string MutationsKey = "MemoryToolkit.Mcp.AllowMutations";
        private const int FirstPort = 8787;
        private const int PortsToTry = 32;
        private const int MaxRequestBytes = 1 << 20;

        /// <summary>
        /// How long a request waits for the main thread. Long enough to survive a
        /// domain reload settling or a big <c>validate_project</c> sweep; short
        /// enough that a wedged Editor fails the call rather than the agent.
        /// </summary>
        private const int MainThreadTimeoutMs = 30_000;

        private static HttpListener _listener;
        private static readonly Queue<PendingCall> Pending = new();
        private static string _token;
        private static int _port;
        private static double _nextHeartbeat;

        static McpServer()
        {
            EditorApplication.update += Pump;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;

            // Deferred: a domain reload runs static constructors before the asset
            // database and EditorApplication state are usable.
            EditorApplication.delayCall += () =>
            {
                if (IsEnabled) Start();
            };
        }

        internal static bool IsEnabled
        {
            get => EditorPrefs.GetBool(EnabledKey, false);
            private set => EditorPrefs.SetBool(EnabledKey, value);
        }

        /// <summary>Whether tools that change Editor state may run. See <see cref="McpTools"/>.</summary>
        internal static bool AllowMutations
        {
            get => EditorPrefs.GetBool(MutationsKey, false);
            set => EditorPrefs.SetBool(MutationsKey, value);
        }

        internal static bool IsRunning => _listener != null && _listener.IsListening;

        internal static int Port => _port;

        /// <summary>Where the endpoint descriptor lives for this project. Under Library/: per-machine, not source.</summary>
        internal static string EndpointFilePath
            => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "MemoryToolkit", "mcp.json"));

        /// <summary>
        /// Mirror of the descriptor in a well-known per-user folder, so the bridge
        /// can find a running Editor without being told which project it is.
        /// </summary>
        internal static string RegistryFilePath
        {
            get
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                return Path.Combine(home, ".memory-toolkit", "endpoints", HashOf(projectPath) + ".json");
            }
        }

        private static void Start()
        {
            if (IsRunning) return;

            _token = NewToken();

            for (int offset = 0; offset < PortsToTry; offset++)
            {
                int port = FirstPort + offset;
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    _port = port;
                    break;
                }
                catch (Exception)
                {
                    // Port taken — commonly a second Editor on the same machine.
                    ((IDisposable)listener).Dispose();
                }
            }

            if (_listener == null)
            {
                Debug.LogError($"[MemoryToolkit MCP] No free port in {FirstPort}..{FirstPort + PortsToTry - 1}. Server not started.");
                return;
            }

            WriteEndpointFiles();
            _listener.BeginGetContext(OnContext, _listener);
            Debug.Log($"[MemoryToolkit MCP] Listening on http://127.0.0.1:{_port}/ (mutations {(AllowMutations ? "enabled" : "disabled")}).");
        }

        private static void Stop()
        {
            if (_listener != null)
            {
                try
                {
                    _listener.Stop();
                    ((IDisposable)_listener).Dispose();
                }
                catch (Exception)
                {
                    // Shutting down; a listener that already faulted is still gone.
                }

                _listener = null;
            }

            // Fail every waiter rather than leaving the request thread blocked on a
            // pump that is about to be replaced by a fresh domain.
            lock (Pending)
            {
                while (Pending.Count > 0)
                {
                    PendingCall call = Pending.Dequeue();
                    call.Error = "Editor server stopped (domain reload or shutdown).";
                    call.Done.Set();
                }
            }

            DeleteEndpointFiles();
            _port = 0;
        }

        // ---- HTTP ------------------------------------------------------------------

        private static void OnContext(IAsyncResult asyncResult)
        {
            var listener = (HttpListener)asyncResult.AsyncState;
            HttpListenerContext context;
            try
            {
                context = listener.EndGetContext(asyncResult);
            }
            catch (Exception)
            {
                return; // Listener stopped.
            }

            try
            {
                listener.BeginGetContext(OnContext, listener);
            }
            catch (Exception)
            {
                // Stopped between accepting this request and queuing the next.
            }

            try
            {
                HandleRequest(context);
            }
            catch (Exception e)
            {
                TryRespond(context, 500, JsonRpcError(JsonValue.Null, -32603, e.Message));
            }
        }

        private static void HandleRequest(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;

            if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                TryRespond(context, 405, JsonRpcError(JsonValue.Null, -32600, "POST only."));
                return;
            }

            string presented = request.Headers["X-MemoryToolkit-Token"];
            if (!TokensMatch(presented, _token))
            {
                TryRespond(context, 401, JsonRpcError(JsonValue.Null, -32001, "Bad or missing token."));
                return;
            }

            if (request.ContentLength64 > MaxRequestBytes)
            {
                TryRespond(context, 413, JsonRpcError(JsonValue.Null, -32600, "Request too large."));
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                body = reader.ReadToEnd();

            if (!JsonValue.TryParse(body, out JsonValue message))
            {
                TryRespond(context, 400, JsonRpcError(JsonValue.Null, -32700, "Parse error."));
                return;
            }

            JsonValue id = message["id"];
            string method = message["method"].AsString();

            switch (method)
            {
                case "ping":
                    TryRespond(context, 200, JsonRpcResult(id, JsonValue.Object().Set("ok", true)));
                    return;

                case "tools/list":
                    Dispatch(context, id, () => McpTools.List());
                    return;

                case "tools/call":
                {
                    string name = message["params"]["name"].AsString();
                    JsonValue arguments = message["params"]["arguments"];
                    Dispatch(context, id, () => McpTools.Call(name, arguments));
                    return;
                }

                default:
                    TryRespond(context, 200, JsonRpcError(id, -32601, $"Unknown method '{method}'."));
                    return;
            }
        }

        /// <summary>Runs <paramref name="work"/> on the main thread and responds with its result.</summary>
        private static void Dispatch(HttpListenerContext context, JsonValue id, Func<JsonValue> work)
        {
            var call = new PendingCall { Work = work };

            lock (Pending)
            {
                if (!IsRunning)
                {
                    TryRespond(context, 503, JsonRpcError(id, -32000, "Editor server is not running."));
                    return;
                }

                Pending.Enqueue(call);
            }

            if (!call.Done.Wait(MainThreadTimeoutMs))
            {
                call.Abandoned = true;
                TryRespond(context, 200, JsonRpcError(id, -32000,
                    "Timed out waiting for the Unity main thread. The Editor is compiling, importing, or blocked by a modal dialog — retry."));
                return;
            }

            TryRespond(context, 200, call.Error != null
                ? JsonRpcError(id, -32000, call.Error)
                : JsonRpcResult(id, call.Result));
        }

        private static void TryRespond(HttpListenerContext context, int status, JsonValue payload)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(payload.ToString());
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.OutputStream.Close();
            }
            catch (Exception)
            {
                // Client hung up; nothing to salvage.
            }
        }

        private static JsonValue JsonRpcResult(JsonValue id, JsonValue result)
            => JsonValue.Object().Set("jsonrpc", "2.0").Set("id", id).Set("result", result);

        private static JsonValue JsonRpcError(JsonValue id, int code, string message)
            => JsonValue.Object()
                .Set("jsonrpc", "2.0")
                .Set("id", id)
                .Set("error", JsonValue.Object().Set("code", code).Set("message", message));

        // ---- Main thread pump ------------------------------------------------------

        private static void Pump()
        {
            while (true)
            {
                PendingCall call;
                lock (Pending)
                {
                    if (Pending.Count == 0) break;
                    call = Pending.Dequeue();
                }

                // The waiter already gave up; running its work would only cost the
                // Editor a frame and possibly mutate state nobody is listening for.
                if (call.Abandoned) continue;

                try
                {
                    call.Result = call.Work();
                }
                catch (Exception e)
                {
                    call.Error = e.Message;
                }

                call.Done.Set();
            }

            if (IsRunning && EditorApplication.timeSinceStartup >= _nextHeartbeat)
            {
                _nextHeartbeat = EditorApplication.timeSinceStartup + 30;
                WriteEndpointFiles();
            }
        }

        private sealed class PendingCall
        {
            internal Func<JsonValue> Work;
            internal JsonValue Result = JsonValue.Null;
            internal string Error;
            internal volatile bool Abandoned;
            internal readonly ManualResetEventSlim Done = new(false);
        }

        // ---- Endpoint discovery ----------------------------------------------------

        private static void WriteEndpointFiles()
        {
            string projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            JsonValue descriptor = JsonValue.Object()
                .Set("port", _port)
                .Set("token", _token)
                .Set("projectPath", projectPath)
                .Set("projectName", Path.GetFileName(projectPath))
                .Set("unityVersion", Application.unityVersion)
                .Set("processId", System.Diagnostics.Process.GetCurrentProcess().Id)
                // Heartbeat: a descriptor left behind by an Editor that was killed
                // is indistinguishable from a live one without it.
                .Set("updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            WriteFile(EndpointFilePath, descriptor.ToString());
            WriteFile(RegistryFilePath, descriptor.ToString());
        }

        private static void WriteFile(string path, string contents)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllText(path, contents);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MemoryToolkit MCP] Could not write {path}: {e.Message}");
            }
        }

        private static void DeleteEndpointFiles()
        {
            foreach (string path in new[] { EndpointFilePath, RegistryFilePath })
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception)
                {
                    // A stale descriptor is handled by the heartbeat check on the
                    // client side; failing to delete it is not worth a log line.
                }
            }
        }

        private static string NewToken()
        {
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static bool TokensMatch(string presented, string expected)
        {
            if (presented == null || expected == null || presented.Length != expected.Length) return false;

            int difference = 0;
            for (int i = 0; i < presented.Length; i++)
                difference |= presented[i] ^ expected[i];
            return difference == 0;
        }

        private static string HashOf(string value)
        {
            using var sha = SHA1.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // ---- Menu ------------------------------------------------------------------

        private const string EnableMenu = "Window/Analysis/Memory Toolkit MCP/Enable Server";
        private const string MutationsMenu = "Window/Analysis/Memory Toolkit MCP/Allow Mutating Tools";
        private const string ConfigMenu = "Window/Analysis/Memory Toolkit MCP/Copy Client Config";

        [MenuItem(EnableMenu)]
        private static void ToggleEnabled()
        {
            IsEnabled = !IsEnabled;
            if (IsEnabled) Start();
            else Stop();
            Menu.SetChecked(EnableMenu, IsEnabled);
        }

        [MenuItem(EnableMenu, validate = true)]
        private static bool ToggleEnabledValidate()
        {
            Menu.SetChecked(EnableMenu, IsEnabled);
            return true;
        }

        [MenuItem(MutationsMenu)]
        private static void ToggleMutations()
        {
            AllowMutations = !AllowMutations;
            Menu.SetChecked(MutationsMenu, AllowMutations);
        }

        [MenuItem(MutationsMenu, validate = true)]
        private static bool ToggleMutationsValidate()
        {
            Menu.SetChecked(MutationsMenu, AllowMutations);
            return true;
        }

        [MenuItem(ConfigMenu)]
        private static void CopyClientConfig()
        {
            string projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string bridge = BridgeScriptPath();

            string config =
                "{\n" +
                "  \"mcpServers\": {\n" +
                "    \"memory-toolkit\": {\n" +
                "      \"command\": \"node\",\n" +
                $"      \"args\": [\"{bridge.Replace("\\", "\\\\")}\", \"--project\", \"{projectPath.Replace("\\", "\\\\")}\"]\n" +
                "    }\n" +
                "  }\n" +
                "}";

            EditorGUIUtility.systemCopyBuffer = config;
            Debug.Log("[MemoryToolkit MCP] Client config copied to the clipboard.\n" + config);
        }

        /// <summary>
        /// Absolute path to the stdio bridge. Resolved through the package manager
        /// rather than from the assembly location — the compiled assembly lives in
        /// <c>Library/ScriptAssemblies</c>, nowhere near the package's <c>Tools~</c>
        /// folder, and the package itself may be embedded, local, or in the cache.
        /// </summary>
        private static string BridgeScriptPath()
        {
            const string relative = "Tools~/memory-toolkit-mcp/index.mjs";
            UnityEditor.PackageManager.PackageInfo package =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(McpServer).Assembly);

            string root = package != null
                ? package.resolvedPath
                : Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(root, relative));
        }
    }
}
