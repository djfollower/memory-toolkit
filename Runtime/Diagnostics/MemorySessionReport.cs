using System.Collections.Generic;
using System.Text;
using MemoryToolkit.Budgets;
using UnityEngine.Profiling;

namespace MemoryToolkit.Diagnostics
{
    /// <summary>
    /// Builds the machine-readable session report — escapes, pool occupancy, managed
    /// heap against the budget ceiling — as JSON.
    ///
    /// <para><b>Why this lives in the runtime assembly.</b> The same report is written
    /// two places: by the CI gate after an automated play session (editor), and by a
    /// soak run on a device (player build). If each built its own JSON they would
    /// drift, and a device dump that a CI reader cannot parse is a device dump nobody
    /// reads. So the one shape is here, where both callers can reach it, and
    /// <c>MemoryToolkitCI.WriteSessionReport</c> delegates to it.</para>
    ///
    /// <para>Escapes is the number the report exists for: instances that reached
    /// <c>PoolBridge.Return</c> owned by no pool, and so were destroyed rather than
    /// pooled. Non-zero means pooling is not working and is costing more than not
    /// pooling at all — the regression a nightly device run is there to catch.</para>
    /// </summary>
    public static class MemorySessionReport
    {
        /// <summary>Schema version, shared with the CI static report. Consumers should check it.</summary>
        public const int JsonSchemaVersion = 1;

        /// <summary>The verdict and the numbers behind it, without serialising.</summary>
        public readonly struct Result
        {
            public Result(bool passed, int escapes, long managedUsedBytes, long managedCeilingBytes)
            {
                Passed = passed;
                Escapes = escapes;
                ManagedUsedBytes = managedUsedBytes;
                ManagedCeilingBytes = managedCeilingBytes;
            }

            /// <summary>False when escapes are non-zero or the heap is over the budget ceiling.</summary>
            public bool Passed { get; }

            public int Escapes { get; }
            public long ManagedUsedBytes { get; }

            /// <summary>0 when no budget, or the budget sets no ceiling for this tier.</summary>
            public long ManagedCeilingBytes { get; }
        }

        /// <summary>
        /// Serialises the current session state, and reports the pass/fail via
        /// <paramref name="result"/>. A <paramref name="budget"/> supplies the heap
        /// ceiling to check against; without one, only escapes decide the verdict.
        /// </summary>
        public static string BuildJson(MemoryBudget budget, MemoryBudgetTier? tier, out Result result)
        {
            MemoryBudgetTier t = tier ?? DeviceTier.Current;
            int escapes = Migration.PoolBridge.UnknownInstanceCount;
            long managedBytes = Profiler.GetMonoUsedSizeLong();

            int ceilingMb = budget != null ? budget.ManagedHeapCeilingMb.Get(t) : 0;
            long ceilingBytes = (long)ceilingMb * 1024 * 1024;
            bool overCeiling = ceilingBytes > 0 && managedBytes > ceilingBytes;
            bool passed = escapes == 0 && !overCeiling;
            result = new Result(passed, escapes, managedBytes, ceilingBytes);

            StringBuilder sb = new StringBuilder(512);
            sb.Append("{\"schemaVersion\":").Append(JsonSchemaVersion)
                .Append(",\"kind\":\"session\"")
                .Append(",\"tier\":\"").Append(t).Append('"')
                .Append(",\"passed\":").Append(passed ? "true" : "false")
                .Append(",\"escapes\":").Append(escapes)
                .Append(",\"gets\":").Append(Migration.PoolBridge.GetCount)
                .Append(",\"returns\":").Append(Migration.PoolBridge.ReturnCount)
                .Append(",\"lazyPools\":").Append(Migration.PoolBridge.LazyPoolCount)
                .Append(",\"managedUsedBytes\":").Append(managedBytes)
                .Append(",\"managedCeilingBytes\":").Append(ceilingBytes)
                .Append(",\"pools\":[");

            var stats = new List<MemoryManager.PoolStat>();
            MemoryManager.GetPoolStats(stats);
            for (int i = 0; i < stats.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"scope\":\"").Append(Escape(stats[i].ScopeName))
                    .Append("\",\"prefab\":\"").Append(Escape(stats[i].PrefabName))
                    .Append("\",\"active\":").Append(stats[i].CountActive)
                    .Append(",\"inactive\":").Append(stats[i].CountInactive)
                    .Append(",\"warmedUp\":").Append(stats[i].WasWarmedUp ? "true" : "false")
                    .Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        internal static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }

            return sb.ToString();
        }
    }
}
