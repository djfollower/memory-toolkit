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
                Name = "triage_project",
                Description =
                    "Runs the adoption triage of docs/ADOPTION.md §1 as data: Instantiate/Destroy churn, the " +
                    "Update/LateUpdate/FixedUpdate census, boot-entry and session-boundary candidates, the hottest " +
                    "churn file, and whether an incumbent pool already exists — which decides whether to follow the " +
                    "ADOPTION or the INTEGRATION guide. The output is a scope map to reason about, not a fix list. " +
                    "Heuristic: every candidate carries the evidence it was drawn from.",
                Schema = Schema(
                    Property("folder", "string", "Folder to triage. Default: the project's Assets folder.")),
                Run = TriageProject,
            },
            new Tool
            {
                Name = "propose_scope_map",
                Description =
                    "Drafts the scope map of docs/ADOPTION.md §2 from a triage: boot entry point → Permanent, the " +
                    "session-teardown class → Scene, per-frame query sites → Frame. A starting point for the one " +
                    "decision the guide says is a human's — assigning lifetimes — not a substitute for it.",
                Schema = Schema(
                    Property("folder", "string", "Folder to triage first. Default: the project's Assets folder.")),
                Run = ProposeScopeMap,
            },
            new Tool
            {
                Name = "suggest_budget",
                Description =
                    "Turns the recorded timeline into a draft MemoryBudget: each pool's peak active becomes its " +
                    "warm-up count, grouped by scope. Requires a recording (recorder_control start, then exercise a " +
                    "representative session). The peak is the warm-up count; the instantaneous count cannot size a pool.",
                Schema = Schema(
                    Property("tier", "string", "Tier to write the peaks into: Low, Medium, or High. Default: High.")),
                Run = SuggestBudget,
            },
            new Tool
            {
                Name = "explain_finding",
                Description =
                    "Maps a finding — a validator issue, a timeline anomaly, an analyzer rule — to the field-guide " +
                    "section that explains why it breaks and what to do. Pass a topic slug; call with no topic to " +
                    "list them. This is how a finding connects back to the method in the guides.",
                Schema = Schema(
                    Property("topic", "string", "Topic slug, e.g. 'stop-action-destroy' or 'escapes'. Omit to list all.")),
                Run = ExplainFinding,
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

            // The sweep itself lives in PoolProjectScan, shared with the CI gate, so
            // the two can never disagree about whether the project is clean. `limit`
            // stays here: it caps how much of the result is *shown* to one caller,
            // which is presentation, not scanning.
            PoolProjectScan.Result scan = PoolProjectScan.Run(new PoolProjectScan.Options
            {
                Folder = folder,
                MinSeverity = minSeverity,
                MaxPrefabs = maxPrefabs,
            });

            JsonValue reports = JsonValue.Array();
            foreach (PoolProjectScan.PrefabReport report in scan.Reports)
            {
                if (reports.Count >= limit) break;

                reports.Add(JsonValue.Object()
                    .Set("assetPath", report.AssetPath)
                    .Set("prefab", report.PrefabName)
                    .Set("errors", report.Errors)
                    .Set("warnings", report.Warnings)
                    .Set("issues", IssuesToJson(report.Issues, minSeverity)));
            }

            return JsonValue.Object()
                .Set("folder", scan.Folder)
                .Set("minSeverity", minSeverity.ToString())
                .Set("prefabsFound", scan.PrefabsFound)
                .Set("prefabsScanned", scan.PrefabsScanned)
                .Set("prefabsWithFindings", scan.PrefabsWithFindings)
                .Set("reported", reports.Count)
                .Set("truncated", scan.PrefabsWithFindings > reports.Count || scan.HitPrefabCap)
                .Set("totalErrors", scan.TotalErrors)
                .Set("totalWarnings", scan.TotalWarnings)
                .Set("reports", reports);
        }

        // ---- Handlers: triage --------------------------------------------------------

        private static JsonValue TriageProject(JsonValue arguments)
        {
            string folder = ResolveTriageFolder(arguments["folder"].AsString(null));
            ProjectTriage.Result triage = ProjectTriage.Run(folder);

            JsonValue boot = JsonValue.Array();
            foreach (ProjectTriage.Evidence e in triage.BootCandidates) boot.Add(EvidenceJson(e));

            JsonValue boundaries = JsonValue.Array();
            foreach (ProjectTriage.Evidence e in triage.SessionBoundaries) boundaries.Add(EvidenceJson(e));

            JsonValue incumbent = JsonValue.Array();
            foreach (ProjectTriage.Evidence e in triage.IncumbentPoolEvidence) incumbent.Add(EvidenceJson(e));

            // The Destroy:Instantiate ratio is a diagnostic in itself — roughly 2:1 is
            // the signature of a consume-two-produce-one loop (merge/match games), the
            // observation ADOPTION §1 grep 3 is built on.
            double ratio = triage.InstantiateCalls > 0
                ? Math.Round((double)triage.DestroyCalls / triage.InstantiateCalls, 2)
                : 0;

            return JsonValue.Object()
                .Set("folder", folder)
                .Set("filesScanned", triage.FilesScanned)
                .Set("recommendedGuide", triage.RecommendedGuide)
                .Set("guideReason", triage.HasIncumbentPool
                    ? "An incumbent pool was detected; churn greps understate the real churn. Follow docs/INTEGRATION.md."
                    : "No pooling found; effectively greenfield. Follow docs/ADOPTION.md.")
                .Set("instantiateCalls", triage.InstantiateCalls)
                .Set("destroyCalls", triage.DestroyCalls)
                .Set("destroyToInstantiateRatio", ratio)
                .Set("filesMentioningPool", triage.FilesMentioningPool)
                .Set("hasIncumbentPool", triage.HasIncumbentPool)
                .Set("updateMethods", triage.UpdateMethods)
                .Set("lateUpdateMethods", triage.LateUpdateMethods)
                .Set("fixedUpdateMethods", triage.FixedUpdateMethods)
                .Set("bootCandidates", boot)
                .Set("sessionBoundaries", boundaries)
                .Set("hottestChurnFile", triage.HottestChurnFile.HasValue
                    ? EvidenceJson(triage.HottestChurnFile.Value)
                    : JsonValue.Null)
                .Set("incumbentPoolEvidence", incumbent)
                .Set("note",
                    "Heuristic triage. Candidates are ranked signals, not answers — the scope map is a human " +
                    "decision (docs/ADOPTION.md §2). Call propose_scope_map for a draft.");
        }

        private static JsonValue ProposeScopeMap(JsonValue arguments)
        {
            string folder = ResolveTriageFolder(arguments["folder"].AsString(null));
            ProjectTriage.Result triage = ProjectTriage.Run(folder);

            JsonValue tiers = JsonValue.Array();

            string permanentOwner = triage.BootCandidates.Count > 0
                ? triage.BootCandidates[0].Text
                : "the boot/app-loader entry point (none found by name — identify it manually)";
            tiers.Add(JsonValue.Object()
                .Set("tier", "Permanent")
                .Set("owner", permanentOwner)
                .Set("contents", "Config services, catalogs, audio, anything reachable from boot and never torn down. " +
                    "Pin configs loaded in a momentary boot scene to Permanent.")
                .Set("confidence", triage.BootCandidates.Count > 0 ? "medium" : "low"));

            string sceneOwner = triage.SessionBoundaries.Count > 0
                ? $"{triage.SessionBoundaries[0].Text} ({System.IO.Path.GetFileName(triage.SessionBoundaries[0].Path)})"
                : "the class whose OnDestroy tears down a play session (none obvious — identify it manually)";
            tiers.Add(JsonValue.Object()
                .Set("tier", "Scene")
                .Set("owner", sceneOwner)
                .Set("contents", "Everything that OnDestroy currently disposes by hand — the natural scope boundary. " +
                    "Create the scope before pooling anything (docs/ADOPTION.md §3 step 1).")
                .Set("confidence", triage.SessionBoundaries.Count > 0 ? "medium" : "low"));

            tiers.Add(JsonValue.Object()
                .Set("tier", "Frame")
                .Set("owner", "per-frame query and physics code")
                .Set("contents", $"{triage.UpdateMethods} Update / {triage.FixedUpdateMethods} FixedUpdate bodies to " +
                    "read for per-frame allocation; move transient scratch into MemoryManager.FrameScratch.")
                .Set("confidence", "low"));

            string firstPrefab = triage.HottestChurnFile.HasValue
                ? System.IO.Path.GetFileName(triage.HottestChurnFile.Value.Path)
                : "the highest-churn prefab";

            return JsonValue.Object()
                .Set("folder", folder)
                .Set("recommendedGuide", triage.RecommendedGuide)
                .Set("tiers", tiers)
                .Set("firstToPool",
                    $"Start with the prefab that churns most — around {firstPrefab}. Pool one, measure, then widen " +
                    "(docs/ADOPTION.md §3 step 2). Confirm the choice with shadow mode, not the call-site count.")
                .Set("note",
                    "A draft. Assigning lifetimes is the decision the guide reserves for a human; this ranks the " +
                    "candidates, it does not make the call.");
        }

        private static JsonValue SuggestBudget(JsonValue arguments)
        {
            string tier = arguments["tier"].AsString("High");
            IReadOnlyList<PoolSeries> series = MemoryRecorder.PoolSeriesList;

            if (series.Count == 0)
            {
                return JsonValue.Object()
                    .Set("error", "No recording. Call recorder_control start, exercise a representative session, " +
                        "then call this — a peak is only as good as the session that produced it.");
            }

            // Group peaks by scope, mirroring the MemoryBudget asset's shape so the
            // output can be transcribed directly, or fed to a budget writer.
            var byScope = new Dictionary<string, JsonValue>();
            foreach (PoolSeries s in series)
            {
                if (s.PeakActive <= 0) continue;

                if (!byScope.TryGetValue(s.ScopeName, out JsonValue pools))
                {
                    pools = JsonValue.Array();
                    byScope[s.ScopeName] = pools;
                }

                pools.Add(JsonValue.Object()
                    .Set("prefab", s.PrefabName)
                    .Set("warmup", s.PeakActive)
                    .Set("maxSize", Math.Max(s.PeakActive, s.PeakActive * 2))
                    .Set("wasWarmedUp", s.WasWarmedUp));
            }

            JsonValue scopes = JsonValue.Array();
            foreach (KeyValuePair<string, JsonValue> kvp in byScope)
                scopes.Add(JsonValue.Object().Set("scopeName", kvp.Key).Set("pools", kvp.Value));

            return JsonValue.Object()
                .Set("tier", tier)
                .Set("scopes", scopes)
                .Set("note",
                    "Draft warm-up counts from this session's peak active per pool. Direct prefab references belong " +
                    "only in the Permanent scope; reference level content by addressable key (docs/BUDGETS.md). " +
                    "Widen the session before trusting the numbers.");
        }

        private static JsonValue ExplainFinding(JsonValue arguments)
        {
            string topic = arguments["topic"].AsString(null);

            if (string.IsNullOrEmpty(topic))
            {
                JsonValue topics = JsonValue.Array();
                foreach (string t in FieldGuideIndex.Topics) topics.Add(JsonValue.String(t));
                return JsonValue.Object()
                    .Set("topics", topics)
                    .Set("note", "Pass one of these as 'topic' to get the guide section that explains it.");
            }

            if (!FieldGuideIndex.TryGet(topic, out FieldGuideIndex.Entry entry))
            {
                JsonValue topics = JsonValue.Array();
                foreach (string t in FieldGuideIndex.Topics) topics.Add(JsonValue.String(t));
                return JsonValue.Object()
                    .Set("error", $"Unknown topic '{topic}'.")
                    .Set("topics", topics);
            }

            return JsonValue.Object()
                .Set("topic", topic)
                .Set("title", entry.Title)
                .Set("guide", entry.Guide)
                .Set("section", entry.Section)
                .Set("why", entry.Summary)
                .Set("action", entry.Action);
        }

        private static string ResolveTriageFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return Application.dataPath;

            // Accept both an absolute path and a project-relative "Assets/..." one, so
            // an agent can pass whichever it has.
            if (System.IO.Path.IsPathRooted(folder)) return folder;

            string trimmed = folder.Replace('\\', '/');
            if (trimmed.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("Assets", StringComparison.OrdinalIgnoreCase))
            {
                string rel = trimmed.Length > "Assets".Length ? trimmed.Substring("Assets".Length).TrimStart('/') : "";
                return string.IsNullOrEmpty(rel) ? Application.dataPath : System.IO.Path.Combine(Application.dataPath, rel);
            }

            return System.IO.Path.Combine(Application.dataPath, trimmed);
        }

        private static JsonValue EvidenceJson(in ProjectTriage.Evidence e)
        {
            // Report paths project-relative — an agent reasons in Assets/... terms, and
            // an absolute path from the triage root just adds noise.
            string path = e.Path.Replace('\\', '/');
            int idx = path.IndexOf("/Assets/", StringComparison.Ordinal);
            if (idx >= 0) path = path.Substring(idx + 1);

            JsonValue json = JsonValue.Object().Set("path", path).Set("evidence", e.Text);
            if (e.Line > 0) json.Set("line", e.Line);
            return json;
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
