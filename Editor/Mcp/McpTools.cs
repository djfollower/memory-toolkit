using System;
using System.Collections.Generic;
using MemoryToolkit.Buffers;
using MemoryToolkit.Diagnostics;
using MemoryToolkit.Migration;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace MemoryToolkit.Editor.Mcp
{
    /// <summary>
    /// The tools <see cref="McpServer"/> exposes, and the single place their
    /// schemas are declared — the stdio bridge fetches this list rather than
    /// carrying its own copy, so a tool cannot drift from its description.
    ///
    /// <para>Everything here runs on the main thread (the server guarantees it)
    /// and returns plain JSON. Handlers answer the questions an agent adopting
    /// pooling actually has: <i>can this prefab be pooled</i>, <i>is it pooled
    /// now</i>, <i>how big should the pool be</i>, and <i>is anything still
    /// escaping the pool</i> — the last two answerable only from the recorder's
    /// history, never from a snapshot.</para>
    /// </summary>
    internal static class McpTools
    {
        private delegate JsonValue Handler(JsonValue arguments);

        private sealed class Tool
        {
            internal string Name;
            internal string Description;
            internal JsonValue Schema;
            internal bool Mutates;
            internal bool RequiresPlayMode;
            internal Handler Run;
        }

        private static readonly List<Tool> Tools = new()
        {
            new Tool
            {
                Name = "editor_status",
                Description =
                    "Unity Editor state: play mode, compiling, active scene, recorder state, and whether mutating " +
                    "tools are enabled. Call this first when another tool reports that it needs play mode.",
                Schema = EmptySchema(),
                Run = _ => EditorStatus(),
            },
            new Tool
            {
                Name = "validate_prefab",
                Description =
                    "Static pool-safety check on one prefab: self-destroying particle systems, physics state that " +
                    "survives reuse, OnDestroy cleanup that stops running once instances are released instead of " +
                    "destroyed, missing scripts, and per-instance vs per-use lifecycle mistakes. Reads prefab data " +
                    "and type metadata, not method bodies — a clean report means 'nothing statically disqualifying', " +
                    "not 'provably correct'.",
                Schema = Schema(
                    Property("assetPath", "string", "Project-relative path, e.g. Assets/Prefabs/Projectile.prefab."),
                    Property("guid", "string", "Asset GUID; an alternative to assetPath.")),
                Run = ValidatePrefab,
            },
            new Tool
            {
                Name = "validate_project",
                Description =
                    "Runs the pool-safety check across every prefab under a folder and returns only those with " +
                    "findings. Use it to triage a codebase before adopting pooling, or to check that a migration " +
                    "left nothing behind.",
                Schema = Schema(
                    Property("folder", "string", "Folder to scan. Default: Assets."),
                    Property("minSeverity", "string", "Error, Warning, or Info. Default: Warning."),
                    Property("limit", "integer", "Maximum prefabs reported. Default: 50."),
                    Property("maxPrefabs", "integer", "Maximum prefabs loaded and scanned. Default: 500.")),
                Run = ValidateProject,
            },
            new Tool
            {
                Name = "get_pool_stats",
                Description =
                    "Live pool occupancy per scope (active / inactive / total, and whether the pool was warmed up " +
                    "or created lazily by a call site), plus the PoolBridge counters: gets, returns, lazily created " +
                    "pools, and escapes — instances that were destroyed rather than pooled.",
                Schema = EmptySchema(),
                Run = _ => PoolStats(),
            },
            new Tool
            {
                Name = "get_memory_snapshot",
                Description =
                    "Heap totals (managed used/reserved, Unity total allocated/reserved) alongside the toolkit's own " +
                    "structure: every live scope with its pools, arenas, pinned assets, and owned disposables, and " +
                    "the frame scratch arena's used / peak / capacity. Size an arena from its peak, not its capacity.",
                Schema = EmptySchema(),
                Run = _ => MemorySnapshot(),
            },
            new Tool
            {
                Name = "recorder_control",
                Description =
                    "Starts, stops, or clears MemoryRecorder. The recorder is off by default. Start it, exercise the " +
                    "transition you care about (a scene load, a match ending), then read get_recorder_timeline — the " +
                    "failures worth finding are transitions, and a snapshot taken afterwards is clean and empty.",
                Schema = Schema(
                    Required("action", "string", "start, stop, or clear."),
                    Property("sampleCapacity", "integer", "Samples retained on start. Default: 480 (two minutes at 4 Hz)."),
                    Property("eventCapacity", "integer", "Events retained on start. Default: 128."),
                    Property("sampleIntervalSeconds", "number", "Seconds between samples. Default: 0.25.")),
                Run = RecorderControl,
            },
            new Tool
            {
                Name = "get_recorder_timeline",
                Description =
                    "The recorded history: per-pool occupancy series with the peak active count (this is the number " +
                    "to warm up to — the instantaneous count cannot size a pool), managed heap and escape rate over " +
                    "time, the event stream, and derived findings such as pools created lazily during gameplay.",
                Schema = Schema(
                    Property("maxSamples", "integer", "Most recent samples per series to include. Default: 60."),
                    Property("includeSamples", "boolean", "Include the raw sample arrays. Default: false — summaries only."),
                    Property("includeEvents", "boolean", "Include the event stream. Default: true.")),
                Run = RecorderTimeline,
            },
            new Tool
            {
                Name = "warmup_pool",
                Description =
                    "Pre-instantiates a prefab's pool in a scope, exactly as a loading screen would. Use it to " +
                    "verify a warm-up count derived from get_recorder_timeline before writing it into game code.",
                Schema = Schema(
                    Required("assetPath", "string", "Project-relative prefab path."),
                    Required("count", "integer", "Instances to pre-instantiate."),
                    Property("maxSize", "integer", "Pool ceiling. Default: 256."),
                    Property("scope", "string", "Live scope name. Default: Permanent.")),
                Mutates = true,
                RequiresPlayMode = true,
                Run = WarmupPool,
            },
            new Tool
            {
                Name = "trim_pools",
                Description =
                    "Trims pooled (inactive) instances, keeping at most keepPerPool in each — what the toolkit does " +
                    "on Application.lowMemory. Active instances are never touched.",
                Schema = Schema(
                    Property("keepPerPool", "integer", "Instances kept per pool. Default: 0."),
                    Property("scope", "string", "Live scope name. Default: every live scope.")),
                Mutates = true,
                RequiresPlayMode = true,
                Run = TrimPools,
            },
            new Tool
            {
                Name = "dispose_scope",
                Description =
                    "Disposes a live scope, freeing its pools, arenas, and registered disposables in reverse " +
                    "registration order — what a scene unload does. Anything still holding an instance from that " +
                    "scope now holds a destroyed object, which is the point: it makes the leak visible.",
                Schema = Schema(Required("name", "string", "Scope name, as reported by get_memory_snapshot.")),
                Mutates = true,
                RequiresPlayMode = true,
                Run = DisposeScope,
            },
            new Tool
            {
                Name = "collect_full",
                Description =
                    "MemoryManager.CollectFull(): a blocking GC plus an unused-assets sweep. In a game this belongs " +
                    "only behind a loading screen; here it is how you check what a scope's disposal actually " +
                    "reclaimed. Freezes the Editor for as long as the collection takes.",
                Schema = EmptySchema(),
                Mutates = true,
                RequiresPlayMode = true,
                Run = _ => CollectFull(),
            },
        };

        // ---- Dispatch ---------------------------------------------------------------

        internal static JsonValue List()
        {
            JsonValue tools = JsonValue.Array();
            foreach (Tool tool in Tools)
            {
                string description = tool.Description;
                if (tool.Mutates)
                {
                    description += tool.RequiresPlayMode
                        ? " Changes Editor state; requires play mode and the 'Allow Mutating Tools' setting."
                        : " Changes Editor state; requires the 'Allow Mutating Tools' setting.";
                }

                tools.Add(JsonValue.Object()
                    .Set("name", tool.Name)
                    .Set("description", description)
                    .Set("inputSchema", tool.Schema));
            }

            return JsonValue.Object().Set("tools", tools);
        }

        internal static JsonValue Call(string name, JsonValue arguments)
        {
            Tool tool = Tools.Find(t => t.Name == name);
            if (tool == null) throw new InvalidOperationException($"Unknown tool '{name}'.");

            if (tool.Mutates && !McpServer.AllowMutations)
            {
                throw new InvalidOperationException(
                    $"'{name}' changes Editor state and mutating tools are disabled. Enable them in " +
                    "Window > Analysis > Memory Toolkit MCP > Allow Mutating Tools.");
            }

            if (tool.RequiresPlayMode && !EditorApplication.isPlaying)
            {
                throw new InvalidOperationException(
                    $"'{name}' acts on live pools and scopes, which exist only in play mode. Enter play mode first.");
            }

            return tool.Run(arguments ?? JsonValue.Null);
        }

        // ---- Handlers: status --------------------------------------------------------

        private static JsonValue EditorStatus()
            => JsonValue.Object()
                .Set("unityVersion", Application.unityVersion)
                .Set("projectName", Application.productName)
                .Set("isPlaying", EditorApplication.isPlaying)
                .Set("isPaused", EditorApplication.isPaused)
                .Set("isCompiling", EditorApplication.isCompiling)
                .Set("activeScene", UnityEngine.SceneManagement.SceneManager.GetActiveScene().name)
                .Set("recording", MemoryRecorder.IsRecording)
                .Set("mutatingToolsEnabled", McpServer.AllowMutations)
                .Set("liveScopeCount", EditorApplication.isPlaying ? MemoryManager.LiveScopes.Count : 0);

        // ---- Handlers: validation ----------------------------------------------------

        private static JsonValue ValidatePrefab(JsonValue arguments)
        {
            GameObject prefab = ResolvePrefab(arguments, out string assetPath);

            var issues = new List<PoolSafetyValidator.Issue>();
            PoolSafetyValidator.Validate(prefab, issues);

            return JsonValue.Object()
                .Set("assetPath", assetPath)
                .Set("prefab", prefab.name)
                .Set("issues", IssuesToJson(issues, PoolSafetyValidator.Severity.Info))
                .Set("errors", CountAtLeast(issues, PoolSafetyValidator.Severity.Error))
                .Set("warnings", CountExactly(issues, PoolSafetyValidator.Severity.Warning))
                .Set("note", "Static checks only: a script calling Destroy(gameObject) on itself is not visible here.");
        }

        private static JsonValue ValidateProject(JsonValue arguments)
        {
            string folder = arguments["folder"].AsString("Assets");
            PoolSafetyValidator.Severity minSeverity = ParseSeverity(arguments["minSeverity"].AsString("Warning"));
            int limit = Mathf.Clamp(arguments["limit"].AsInt(50), 1, 500);
            int maxPrefabs = Mathf.Clamp(arguments["maxPrefabs"].AsInt(500), 1, 5000);

            // FindAssets logs its own error and returns nothing for a bad folder, which
            // reads to an agent as "this folder is clean".
            if (folder != "Assets" && !AssetDatabase.IsValidFolder(folder))
                throw new InvalidOperationException($"'{folder}' is not a folder in this project.");

            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { folder });
            int scanned = 0, withFindings = 0, errors = 0, warnings = 0;

            JsonValue reports = JsonValue.Array();
            var issues = new List<PoolSafetyValidator.Issue>();

            foreach (string guid in guids)
            {
                if (scanned >= maxPrefabs) break;

                string path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                scanned++;
                issues.Clear();
                PoolSafetyValidator.Validate(prefab, issues);

                int prefabErrors = CountAtLeast(issues, PoolSafetyValidator.Severity.Error);
                int prefabWarnings = CountExactly(issues, PoolSafetyValidator.Severity.Warning);
                errors += prefabErrors;
                warnings += prefabWarnings;

                JsonValue filtered = IssuesToJson(issues, minSeverity);
                if (filtered.Count == 0) continue;

                withFindings++;
                if (reports.Count >= limit) continue;

                reports.Add(JsonValue.Object()
                    .Set("assetPath", path)
                    .Set("prefab", prefab.name)
                    .Set("errors", prefabErrors)
                    .Set("warnings", prefabWarnings)
                    .Set("issues", filtered));
            }

            return JsonValue.Object()
                .Set("folder", folder)
                .Set("minSeverity", minSeverity.ToString())
                .Set("prefabsFound", guids.Length)
                .Set("prefabsScanned", scanned)
                .Set("prefabsWithFindings", withFindings)
                .Set("reported", reports.Count)
                .Set("truncated", withFindings > reports.Count || guids.Length > scanned)
                .Set("totalErrors", errors)
                .Set("totalWarnings", warnings)
                .Set("reports", reports);
        }

        // ---- Handlers: live state ----------------------------------------------------

        private static JsonValue PoolStats()
        {
            var stats = new List<MemoryManager.PoolStat>();
            if (EditorApplication.isPlaying) MemoryManager.GetPoolStats(stats);

            JsonValue pools = JsonValue.Array();
            foreach (MemoryManager.PoolStat stat in stats)
            {
                pools.Add(JsonValue.Object()
                    .Set("scope", stat.ScopeName)
                    .Set("prefab", stat.PrefabName)
                    .Set("active", stat.CountActive)
                    .Set("inactive", stat.CountInactive)
                    .Set("total", stat.CountAll)
                    .Set("warmedUp", stat.WasWarmedUp));
            }

            return JsonValue.Object()
                .Set("isPlaying", EditorApplication.isPlaying)
                .Set("pools", pools)
                .Set("bridge", JsonValue.Object()
                    .Set("gets", PoolBridge.GetCount)
                    .Set("returns", PoolBridge.ReturnCount)
                    .Set("lazyPools", PoolBridge.LazyPoolCount)
                    .Set("escapes", PoolBridge.UnknownInstanceCount)
                    .Set("escapesMeaning",
                        "Instances that reached PoolBridge.Return owned by no toolkit pool — destroyed instead of " +
                        "pooled. The rate a pool exists to drive to zero."));
        }

        private static JsonValue MemorySnapshot()
        {
            JsonValue heap = JsonValue.Object()
                .Set("managedUsedBytes", Profiler.GetMonoUsedSizeLong())
                .Set("managedReservedBytes", Profiler.GetMonoHeapSizeLong())
                .Set("totalAllocatedBytes", Profiler.GetTotalAllocatedMemoryLong())
                .Set("totalReservedBytes", Profiler.GetTotalReservedMemoryLong());

            JsonValue scopes = JsonValue.Array();
            if (EditorApplication.isPlaying)
            {
                var stats = new List<MemoryManager.PoolStat>();
                MemoryManager.GetPoolStats(stats);

                foreach (MemoryScope scope in MemoryManager.LiveScopes)
                {
                    JsonValue pools = JsonValue.Array();
                    foreach (MemoryManager.PoolStat stat in stats)
                    {
                        if (stat.ScopeName != scope.Name) continue;
                        pools.Add(JsonValue.Object()
                            .Set("prefab", stat.PrefabName)
                            .Set("active", stat.CountActive)
                            .Set("inactive", stat.CountInactive)
                            .Set("warmedUp", stat.WasWarmedUp));
                    }

                    JsonValue arenas = JsonValue.Array();
                    foreach (FrameAllocator allocator in scope.Allocators)
                        arenas.Add(ArenaToJson(allocator));

                    JsonValue pinned = JsonValue.Array();
                    foreach (UnityEngine.Object asset in scope.PinnedAssets)
                        pinned.Add(JsonValue.String(asset == null ? "<destroyed>" : asset.name));

                    scopes.Add(JsonValue.Object()
                        .Set("name", scope.Name)
                        .Set("pools", pools)
                        .Set("arenas", arenas)
                        .Set("pinnedAssets", pinned)
                        .Set("ownedDisposables", scope.OwnedDisposableCount));
                }
            }

            FrameAllocator scratch = MemoryManager.FrameScratchOrNull;

            return JsonValue.Object()
                .Set("isPlaying", EditorApplication.isPlaying)
                .Set("heap", heap)
                .Set("scopes", scopes)
                // Read without creating: touching MemoryManager.FrameScratch would
                // allocate a megabyte of native memory in a game that never used it.
                .Set("frameScratch", scratch == null
                    ? JsonValue.Object().Set("allocated", false)
                    : ArenaToJson(scratch).Set("allocated", true));
        }

        private static JsonValue ArenaToJson(FrameAllocator allocator)
            => JsonValue.Object()
                .Set("usedBytes", allocator.UsedBytes)
                .Set("peakUsedBytes", allocator.PeakUsedBytes)
                .Set("capacityBytes", allocator.CapacityBytes);

        // ---- Handlers: recorder ------------------------------------------------------

        private static JsonValue RecorderControl(JsonValue arguments)
        {
            string action = arguments["action"].AsString("")?.ToLowerInvariant();

            switch (action)
            {
                case "start":
                    if (arguments.Has("sampleIntervalSeconds"))
                        MemoryRecorder.SampleIntervalSeconds = Math.Max(0.01, arguments["sampleIntervalSeconds"].AsDouble(0.25));
                    MemoryRecorder.Enable(
                        Mathf.Clamp(arguments["sampleCapacity"].AsInt(480), 8, 100_000),
                        Mathf.Clamp(arguments["eventCapacity"].AsInt(128), 8, 100_000));
                    break;
                case "stop":
                    MemoryRecorder.Disable();
                    break;
                case "clear":
                    MemoryRecorder.Clear();
                    break;
                default:
                    throw new InvalidOperationException($"Unknown action '{action}'. Expected start, stop, or clear.");
            }

            return JsonValue.Object()
                .Set("action", action)
                .Set("recording", MemoryRecorder.IsRecording)
                .Set("sampleIntervalSeconds", MemoryRecorder.SampleIntervalSeconds)
                .Set("isPlaying", EditorApplication.isPlaying)
                .Set("note", EditorApplication.isPlaying
                    ? "Sampling runs from the toolkit's LateUpdate; history accumulates while play mode runs."
                    : "Recording is armed, but samples are only taken in play mode — enter play mode to record.");
        }

        private static JsonValue RecorderTimeline(JsonValue arguments)
        {
            int maxSamples = Mathf.Clamp(arguments["maxSamples"].AsInt(60), 1, 5000);
            bool includeSamples = arguments["includeSamples"].AsBool(false);
            bool includeEvents = arguments["includeEvents"].AsBool(true);

            MemoryRing<GlobalSample> global = MemoryRecorder.GlobalSamples;
            MemoryRing<MemoryEvent> events = MemoryRecorder.Events;
            IReadOnlyList<PoolSeries> series = MemoryRecorder.PoolSeriesList;

            var findings = JsonValue.Array();

            JsonValue globalJson = JsonValue.Object();
            if (global != null && global.Count > 0)
            {
                int escapes = 0, gets = 0, returns = 0, lazyPools = 0, peakScopes = 0;
                for (int i = 0; i < global.Count; i++)
                {
                    ref GlobalSample sample = ref global[i];
                    escapes += sample.EscapeDelta;
                    gets += sample.GetDelta;
                    returns += sample.ReturnDelta;
                    lazyPools += sample.LazyPoolDelta;
                    if (sample.ScopeCount > peakScopes) peakScopes = sample.ScopeCount;
                }

                ref GlobalSample last = ref global[global.Count - 1];
                globalJson
                    .Set("sampleCount", global.Count)
                    .Set("windowSeconds", last.Time - global[0].Time)
                    .Set("managedUsedBytes", last.ManagedUsedBytes)
                    .Set("liveScopes", last.ScopeCount)
                    .Set("peakLiveScopes", peakScopes)
                    .Set("escapesInWindow", escapes)
                    .Set("getsInWindow", gets)
                    .Set("returnsInWindow", returns)
                    .Set("lazyPoolsInWindow", lazyPools);

                if (escapes > 0)
                {
                    findings.Add(Finding("escapes",
                        $"{escapes} instance(s) were destroyed instead of pooled in this window. Something returns " +
                        "instances the toolkit does not own — usually a call site still using Instantiate, or a " +
                        "prefab reference that differs from the pooled one."));
                }

                if (lazyPools > 0)
                {
                    findings.Add(Finding("lazy-pools",
                        $"{lazyPools} pool(s) were created on a first Get during this window rather than by Warmup. " +
                        "Their capacity came from a call site's guess and their first spawn cost an Instantiate " +
                        "during gameplay."));
                }

                if (includeSamples)
                {
                    JsonValue samples = JsonValue.Array();
                    for (int i = Math.Max(0, global.Count - maxSamples); i < global.Count; i++)
                    {
                        ref GlobalSample sample = ref global[i];
                        samples.Add(JsonValue.Object()
                            .Set("t", Round(sample.Time - MemoryRecorder.StartTime))
                            .Set("managedUsedBytes", sample.ManagedUsedBytes)
                            .Set("scopes", sample.ScopeCount)
                            .Set("frameScratchUsedBytes", sample.FrameScratchUsedBytes)
                            .Set("escapes", sample.EscapeDelta));
                    }

                    globalJson.Set("samples", samples);
                }
            }

            JsonValue pools = JsonValue.Array();
            foreach (PoolSeries pool in series)
            {
                if (pool.Samples.Count == 0) continue;

                ref PoolSample last = ref pool.Samples[pool.Samples.Count - 1];
                JsonValue poolJson = JsonValue.Object()
                    .Set("scope", pool.ScopeName)
                    .Set("prefab", pool.PrefabName)
                    .Set("alive", pool.Alive)
                    .Set("warmedUp", pool.WasWarmedUp)
                    .Set("active", last.Active)
                    .Set("inactive", last.Inactive)
                    .Set("peakActive", pool.PeakActive)
                    .Set("suggestedWarmupCount", pool.PeakActive);

                if (includeSamples)
                {
                    JsonValue samples = JsonValue.Array();
                    for (int i = Math.Max(0, pool.Samples.Count - maxSamples); i < pool.Samples.Count; i++)
                    {
                        ref PoolSample sample = ref pool.Samples[i];
                        samples.Add(JsonValue.Object()
                            .Set("active", sample.Active)
                            .Set("inactive", sample.Inactive)
                            .Set("alive", sample.Alive));
                    }

                    poolJson.Set("samples", samples);
                }

                pools.Add(poolJson);

                if (!pool.WasWarmedUp && pool.PeakActive > 0)
                {
                    findings.Add(Finding("not-warmed",
                        $"{pool.ScopeName}/{pool.PrefabName} peaked at {pool.PeakActive} active instance(s) and was " +
                        $"never warmed up. Warm it to {pool.PeakActive} during the load that precedes its use."));
                }
            }

            JsonValue eventsJson = JsonValue.Array();
            if (includeEvents && events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    ref MemoryEvent recorded = ref events[i];
                    eventsJson.Add(JsonValue.Object()
                        .Set("t", Round(recorded.Time - MemoryRecorder.StartTime))
                        .Set("kind", recorded.Kind.ToString())
                        .Set("label", recorded.Label)
                        .Set("value", recorded.Value));
                }
            }

            return JsonValue.Object()
                .Set("recording", MemoryRecorder.IsRecording)
                .Set("hasHistory", global != null && global.Count > 0)
                .Set("global", globalJson)
                .Set("pools", pools)
                .Set("events", eventsJson)
                .Set("findings", findings)
                .Set("note", global == null || global.Count == 0
                    ? "No history: start the recorder (recorder_control) and enter play mode, then exercise the transition."
                    : "peakActive across the retained window is the number to warm up to; the instantaneous active " +
                      "count cannot size a pool.");
        }

        // ---- Handlers: mutations -----------------------------------------------------

        private static JsonValue WarmupPool(JsonValue arguments)
        {
            GameObject prefab = ResolvePrefab(arguments, out string assetPath);
            int count = arguments["count"].AsInt(0);
            if (count <= 0) throw new InvalidOperationException("'count' must be greater than zero.");

            int maxSize = Math.Max(count, arguments["maxSize"].AsInt(256));
            MemoryScope scope = ResolveScope(arguments["scope"].AsString());

            scope.Warmup(prefab, count, maxSize);

            return JsonValue.Object()
                .Set("assetPath", assetPath)
                .Set("scope", scope.Name)
                .Set("warmedUp", count)
                .Set("maxSize", maxSize);
        }

        private static JsonValue TrimPools(JsonValue arguments)
        {
            int keep = Math.Max(0, arguments["keepPerPool"].AsInt(0));
            string scopeName = arguments["scope"].AsString();

            var before = new List<MemoryManager.PoolStat>();
            MemoryManager.GetPoolStats(before);
            int inactiveBefore = 0;
            foreach (MemoryManager.PoolStat stat in before) inactiveBefore += stat.CountInactive;

            JsonValue trimmed = JsonValue.Array();
            if (scopeName == null)
            {
                foreach (MemoryScope scope in MemoryManager.LiveScopes)
                {
                    scope.Trim(keep);
                    trimmed.Add(JsonValue.String(scope.Name));
                }
            }
            else
            {
                MemoryScope scope = ResolveScope(scopeName);
                scope.Trim(keep);
                trimmed.Add(JsonValue.String(scope.Name));
            }

            var after = new List<MemoryManager.PoolStat>();
            MemoryManager.GetPoolStats(after);
            int inactiveAfter = 0;
            foreach (MemoryManager.PoolStat stat in after) inactiveAfter += stat.CountInactive;

            return JsonValue.Object()
                .Set("scopesTrimmed", trimmed)
                .Set("keepPerPool", keep)
                .Set("inactiveBefore", inactiveBefore)
                .Set("inactiveAfter", inactiveAfter)
                .Set("instancesDestroyed", inactiveBefore - inactiveAfter);
        }

        private static JsonValue DisposeScope(JsonValue arguments)
        {
            string name = arguments["name"].AsString();
            if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("'name' is required.");

            MemoryScope scope = ResolveScope(name);
            int poolCount = 0;
            var stats = new List<MemoryManager.PoolStat>();
            MemoryManager.GetPoolStats(stats);
            foreach (MemoryManager.PoolStat stat in stats)
            {
                if (stat.ScopeName == scope.Name) poolCount++;
            }

            scope.Dispose();

            return JsonValue.Object()
                .Set("scope", name)
                .Set("poolsFreed", poolCount)
                .Set("liveScopesRemaining", MemoryManager.LiveScopes.Count);
        }

        private static JsonValue CollectFull()
        {
            long before = Profiler.GetMonoUsedSizeLong();
            MemoryManager.CollectFull();
            long after = Profiler.GetMonoUsedSizeLong();

            return JsonValue.Object()
                .Set("managedUsedBytesBefore", before)
                .Set("managedUsedBytesAfter", after)
                .Set("reclaimedBytes", before - after)
                .Set("note",
                    "Resources.UnloadUnusedAssets is asynchronous: asset memory it reclaims lands after this call " +
                    "returns, so re-read get_memory_snapshot a moment later for the full picture.");
        }

        // ---- Shared helpers ----------------------------------------------------------

        private static GameObject ResolvePrefab(JsonValue arguments, out string assetPath)
        {
            assetPath = arguments["assetPath"].AsString();
            string guid = arguments["guid"].AsString();

            if (string.IsNullOrEmpty(assetPath) && !string.IsNullOrEmpty(guid))
                assetPath = AssetDatabase.GUIDToAssetPath(guid);

            if (string.IsNullOrEmpty(assetPath))
                throw new InvalidOperationException("Provide 'assetPath' or 'guid'.");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
                throw new InvalidOperationException($"No prefab at '{assetPath}'.");

            return prefab;
        }

        /// <summary>
        /// Scopes are addressed by name because that is what every diagnostic
        /// surface reports. Names are not unique — two scenes can be loaded twice —
        /// so an ambiguous name is an error rather than a coin flip, since the
        /// mutating tools would otherwise dispose the wrong one.
        /// </summary>
        private static MemoryScope ResolveScope(string name)
        {
            if (string.IsNullOrEmpty(name)) return MemoryManager.Permanent;

            MemoryScope found = null;
            int matches = 0;
            foreach (MemoryScope scope in MemoryManager.LiveScopes)
            {
                if (scope.Name != name) continue;
                found = scope;
                matches++;
            }

            if (matches == 0)
            {
                var names = new List<string>();
                foreach (MemoryScope scope in MemoryManager.LiveScopes) names.Add(scope.Name);
                throw new InvalidOperationException($"No live scope named '{name}'. Live scopes: {string.Join(", ", names)}.");
            }

            if (matches > 1)
                throw new InvalidOperationException($"{matches} live scopes are named '{name}'; refusing to guess which one.");

            return found;
        }

        private static JsonValue IssuesToJson(List<PoolSafetyValidator.Issue> issues, PoolSafetyValidator.Severity minSeverity)
        {
            JsonValue result = JsonValue.Array();
            foreach (PoolSafetyValidator.Issue issue in issues)
            {
                // Severity is ordered most-severe-first, so "at least" is "<=".
                if (issue.Severity > minSeverity) continue;
                result.Add(JsonValue.Object()
                    .Set("severity", issue.Severity.ToString())
                    .Set("path", issue.Path)
                    .Set("message", issue.Message));
            }

            return result;
        }

        private static int CountAtLeast(List<PoolSafetyValidator.Issue> issues, PoolSafetyValidator.Severity severity)
        {
            int count = 0;
            foreach (PoolSafetyValidator.Issue issue in issues)
            {
                if (issue.Severity <= severity) count++;
            }

            return count;
        }

        private static int CountExactly(List<PoolSafetyValidator.Issue> issues, PoolSafetyValidator.Severity severity)
        {
            int count = 0;
            foreach (PoolSafetyValidator.Issue issue in issues)
            {
                if (issue.Severity == severity) count++;
            }

            return count;
        }

        private static PoolSafetyValidator.Severity ParseSeverity(string value)
            => Enum.TryParse(value, ignoreCase: true, out PoolSafetyValidator.Severity severity)
                ? severity
                : PoolSafetyValidator.Severity.Warning;

        private static JsonValue Finding(string kind, string message)
            => JsonValue.Object().Set("kind", kind).Set("message", message);

        private static double Round(double value) => Math.Round(value, 2);

        // ---- Schema construction ------------------------------------------------------

        private static JsonValue EmptySchema()
            => JsonValue.Object().Set("type", "object").Set("properties", JsonValue.Object());

        private static JsonValue Schema(params KeyValuePair<string, JsonValue>[] properties)
        {
            JsonValue props = JsonValue.Object();
            JsonValue required = JsonValue.Array();

            foreach (KeyValuePair<string, JsonValue> property in properties)
            {
                props.Set(property.Key, property.Value);
                if (property.Value["required"].AsBool()) required.Add(JsonValue.String(property.Key));
            }

            // The marker is stripped: `required` is a schema-level array, not a
            // property-level flag, and a stray keyword confuses strict validators.
            foreach (KeyValuePair<string, JsonValue> property in props.Members)
                property.Value.Remove("required");

            JsonValue schema = JsonValue.Object().Set("type", "object").Set("properties", props);
            if (required.Count > 0) schema.Set("required", required);
            return schema;
        }

        private static KeyValuePair<string, JsonValue> Property(string name, string type, string description)
            => new(name, JsonValue.Object().Set("type", type).Set("description", description));

        private static KeyValuePair<string, JsonValue> Required(string name, string type, string description)
            => new(name, JsonValue.Object().Set("type", type).Set("description", description).Set("required", true));
    }
}
