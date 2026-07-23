using System.Collections.Generic;
using System.Text;
using MemoryToolkit.Buffers;
using UnityEngine;

namespace MemoryToolkit.Migration
{
    /// <summary>
    /// Measures what pooling <i>would</i> save, without pooling anything.
    ///
    /// <para><b>Why this exists.</b> Adopting pooling in a real project is a sprint,
    /// not an afternoon — the component-at-spawn-time refactor alone (see
    /// <c>docs/ADOPTION.md</c> §4) is usually the largest piece. Sprints have to be
    /// argued for, and the argument needs a number. Everything else in this package
    /// can only produce that number <i>after</i> the migration it is supposed to
    /// justify: pool stats require pools.</para>
    ///
    /// <para>With <see cref="PoolBridge.Mode"/> set to
    /// <see cref="PoolBridgeMode.Observe"/>, the bridge instantiates and destroys
    /// exactly as the code did before — behaviour is unchanged, no pool is created,
    /// nothing is recycled — while this type counts what a pool would have absorbed.
    /// The output is per prefab: instantiates avoided, destroys avoided, and the
    /// <b>peak concurrent live count</b>, which is the warm-up size. Nothing else can
    /// produce that peak before a migration, and an instantaneous count cannot be
    /// substituted for it.</para>
    ///
    /// <para>This also makes the seam useful to greenfield projects. A codebase with
    /// no pool at all can route its <c>Instantiate</c>/<c>Destroy</c> pairs through
    /// <see cref="PoolBridge"/> in Observe mode, ship that, measure a real session,
    /// and only then decide what to pool — so <see cref="PoolBridge"/> stops being a
    /// brownfield-only tool and becomes the general adoption seam.</para>
    /// </summary>
    public static class PoolShadow
    {
        /// <summary>Version of <see cref="ReportJson"/>'s schema. Consumers should check it.</summary>
        public const int JsonSchemaVersion = 1;

        private const string ScopeLabel = "(shadow)";

        private static readonly List<ShadowEntry> EntryList = new();
        private static readonly Dictionary<GameObject, int> EntryIds = new();

        /// <summary>Per-prefab measurements, in first-seen order.</summary>
        public static IReadOnlyList<ShadowEntry> Entries => EntryList;

        /// <summary>Wall-clock seconds since observation began, for rates in the report.</summary>
        public static double ElapsedSeconds => _startTime > 0 ? Now - _startTime : 0;

        /// <summary>
        /// Instances that reached <see cref="PoolBridge.Return"/> during observation
        /// carrying no shadow marker — they were created somewhere this bridge cannot
        /// see. A large number means the projection below understates the real churn,
        /// and that the call sites creating them have to be found before sizing
        /// anything.
        /// </summary>
        public static int UnattributedReturnCount { get; private set; }

        /// <summary>
        /// Returns of an instance that was already returned once. A defect in the
        /// caller, and worth finding here: once pooling is on, the same call site
        /// pushes an instance onto a free list twice.
        ///
        /// <para><b>This is a floor.</b> Observe mode destroys on return, and Unity's
        /// <c>Destroy</c> is deferred to the end of the frame — so only a second
        /// return arriving inside that window can still be attributed. A later one
        /// finds a destroyed object and is reported as a plain false from
        /// <see cref="PoolBridge.Return"/>. Same-frame double release is the common
        /// shape (two systems both "cleaning up" the same object), which is why the
        /// check is worth having despite the gap.</para>
        /// </summary>
        public static int DoubleReturnCount { get; private set; }

        private static double _startTime;

        /// <summary>Drops all measurements and restarts the clock. Call between measured runs.</summary>
        public static void Reset()
        {
            EntryList.Clear();
            EntryIds.Clear();
            UnattributedReturnCount = 0;
            DoubleReturnCount = 0;
            _startTime = Now;
        }

        // ---- Bridge-facing surface --------------------------------------------------

        internal static void Begin()
        {
            if (_startTime <= 0) _startTime = Now;
        }

        internal static GameObject Take(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent)
        {
            ShadowEntry entry = EntryFor(prefab);

            // Exactly what the un-pooled call site did. Observe mode is not allowed to
            // change behaviour — if it did, the measurement would be of a different game.
            GameObject instance = Object.Instantiate(prefab, position, rotation, parent);

            var marker = instance.AddComponent<ShadowInstance>();
            marker.EntryId = entry.Id;

            entry.Gets++;
            entry.Live++;
            if (entry.Live > entry.PeakConcurrent) entry.PeakConcurrent = entry.Live;
            return instance;
        }

        /// <summary>
        /// Destroys the instance as the un-pooled path would, attributing it if it
        /// carries a marker. Returns true when it was attributed.
        /// </summary>
        internal static bool Give(GameObject instance)
        {
            bool attributed = false;

            if (instance.TryGetComponent(out ShadowInstance marker) && marker.EntryId >= 0)
            {
                if (marker.Returned)
                {
                    DoubleReturnCount++;
                    return true;
                }

                marker.Returned = true;
                ShadowEntry entry = EntryList[marker.EntryId];
                entry.Returns++;
                if (entry.Live > 0) entry.Live--;
                attributed = true;
            }
            else
            {
                UnattributedReturnCount++;
            }

            if (Application.isPlaying) Object.Destroy(instance);
            else Object.DestroyImmediate(instance);
            return attributed;
        }

        /// <summary>
        /// Appends one row per observed prefab, shaped as a pool stat so the recorder
        /// and the Inspector's timeline draw shadow prefabs with no special case. The
        /// peak marker the timeline already draws on a pool row is, for these rows,
        /// precisely the warm-up count the prefab would need.
        ///
        /// <para>Must not allocate: it runs on the recorder's sampling tick, which is
        /// asserted to produce zero garbage.</para>
        /// </summary>
        internal static void CollectStats(List<MemoryManager.PoolStat> results)
        {
            for (int i = 0; i < EntryList.Count; i++)
            {
                ShadowEntry entry = EntryList[i];
                results.Add(new MemoryManager.PoolStat
                {
                    ScopeName = ScopeLabel,
                    PrefabName = entry.PrefabName,
                    CountActive = entry.Live,

                    // No instance is ever retained, which is the entire point: an
                    // inactive count of zero next to a rising peak is the shape of
                    // churn that a pool would flatten.
                    CountInactive = 0,
                    CountAll = entry.Live,
                    WasWarmedUp = false,
                });
            }
        }

        // ---- Reports ----------------------------------------------------------------

        /// <summary>Plain-text projection, for device logs, CI output, and pasting into a ticket.</summary>
        public static string Report()
        {
            StringBuilder sb = StringBuilderCache.Acquire(1024);
            sb.Append("[MemoryToolkit] shadow report — ")
                .Append(ElapsedSeconds.ToString("0.0")).Append(" s observed, ")
                .Append(EntryList.Count).AppendLine(" prefab(s)");

            if (EntryList.Count == 0)
            {
                // Deliberately not an early return: a run with no gets but a pile of
                // unattributed returns is a real and confusing state — call sites
                // routed on the return side only — and it is precisely the case where
                // the warnings below are the entire diagnosis.
                sb.AppendLine("  no gets observed — is PoolBridge.Mode set to Observe, and are call sites routed through it?");
            }
            else
            {
                sb.AppendLine("  prefab                          gets   returns      peak   unreturned");
                int totalGets = 0, totalReturns = 0, totalPeak = 0;

                for (int i = 0; i < EntryList.Count; i++)
                {
                    ShadowEntry e = EntryList[i];
                    totalGets += e.Gets;
                    totalReturns += e.Returns;
                    totalPeak += e.PeakConcurrent;

                    sb.Append("  ").Append(e.PrefabName.PadRight(30))
                        .Append(e.Gets.ToString().PadLeft(6))
                        .Append(e.Returns.ToString().PadLeft(10))
                        .Append(e.PeakConcurrent.ToString().PadLeft(10))
                        .Append((e.Gets - e.Returns).ToString().PadLeft(13))
                        .AppendLine();
                }

                sb.Append("  projected: ").Append(totalGets).Append(" Instantiate and ").Append(totalReturns)
                    .AppendLine(" Destroy calls avoidable");
                sb.Append("  projected warm-up total: ").Append(totalPeak).AppendLine(" instance(s) across all prefabs");
            }

            if (UnattributedReturnCount > 0)
            {
                sb.Append("  WARNING: ").Append(UnattributedReturnCount)
                    .AppendLine(" return(s) unattributed — instances created outside the bridge; this projection is a floor, not a total");
            }

            if (DoubleReturnCount > 0)
            {
                sb.Append("  WARNING: ").Append(DoubleReturnCount)
                    .AppendLine(" double return(s) — a call site releases twice; fix before pooling, where it corrupts the free list");
            }

            return StringBuilderCache.GetStringAndRelease(sb);
        }

        /// <summary>
        /// The same projection as machine-readable JSON, for CI artifacts and for
        /// seeding a budget asset. <c>peakConcurrent</c> is the warm-up count.
        /// </summary>
        public static string ReportJson()
        {
            StringBuilder sb = StringBuilderCache.Acquire(1024);
            sb.Append("{\"schemaVersion\":").Append(JsonSchemaVersion)
                .Append(",\"elapsedSeconds\":").Append(ElapsedSeconds.ToString("0.000"))
                .Append(",\"unattributedReturns\":").Append(UnattributedReturnCount)
                .Append(",\"doubleReturns\":").Append(DoubleReturnCount)
                .Append(",\"prefabs\":[");

            for (int i = 0; i < EntryList.Count; i++)
            {
                ShadowEntry e = EntryList[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"prefab\":\"");
                AppendEscaped(sb, e.PrefabName);
                sb.Append("\",\"gets\":").Append(e.Gets)
                    .Append(",\"returns\":").Append(e.Returns)
                    .Append(",\"peakConcurrent\":").Append(e.PeakConcurrent)
                    .Append(",\"unreturned\":").Append(e.Gets - e.Returns)
                    .Append('}');
            }

            sb.Append("]}");
            return StringBuilderCache.GetStringAndRelease(sb);
        }

        // ---- Internals ---------------------------------------------------------------

        private static ShadowEntry EntryFor(GameObject prefab)
        {
            if (EntryIds.TryGetValue(prefab, out int id))
                return EntryList[id];

            var entry = new ShadowEntry(EntryList.Count, prefab.name);
            EntryIds.Add(prefab, EntryList.Count);
            EntryList.Add(entry);
            if (_startTime <= 0) _startTime = Now;
            return entry;
        }

        private static void AppendEscaped(StringBuilder sb, string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
        }

        private static double Now => Time.realtimeSinceStartupAsDouble;
    }

    /// <summary>One prefab's shadow measurements. See <see cref="PoolShadow"/>.</summary>
    public sealed class ShadowEntry
    {
        internal ShadowEntry(int id, string prefabName)
        {
            Id = id;
            PrefabName = prefabName;
        }

        internal int Id { get; }

        /// <summary>
        /// Captured at first sight, not read from the prefab on demand: the prefab
        /// may be an Addressable that is unloaded long before the report is written,
        /// and a report that throws at the end of a soak run is worthless.
        /// </summary>
        public string PrefabName { get; }

        /// <summary>Instantiates that a pool would have served from its free list.</summary>
        public int Gets { get; internal set; }

        /// <summary>Destroys that a pool would have absorbed as a release.</summary>
        public int Returns { get; internal set; }

        /// <summary>Live right now — never retained, because Observe mode pools nothing.</summary>
        public int Live { get; internal set; }

        /// <summary>
        /// High-water mark of concurrent live instances. <b>This is the warm-up
        /// count</b>, and it is the one number a pre-migration codebase cannot
        /// otherwise produce.
        /// </summary>
        public int PeakConcurrent { get; internal set; }
    }
}
