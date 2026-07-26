using System.Collections.Generic;
using System.Text;
using MemoryToolkit.Buffers;
using UnityEngine.Profiling;

namespace MemoryToolkit.Diagnostics
{
    /// <summary>
    /// A destination for memory breadcrumbs — typically a crash reporter's custom-key
    /// API. The default is a no-op, so nothing is sent until a game opts in.
    /// </summary>
    public interface IBreadcrumbSink
    {
        /// <summary>Records a key/value pair to attach to the next crash report.</summary>
        void Set(string key, string value);
    }

    /// <summary>
    /// Pushes a small, fixed set of memory facts into a crash reporter so an OOM
    /// arrives with a memory postmortem attached instead of a bare stack trace.
    ///
    /// <para><b>Why a fixed, small set.</b> Crash reporters cap custom keys hard —
    /// Crashlytics allows 64 keys of 1 KB each — and a payload that blows the cap is
    /// silently truncated, dropping exactly the fields you added. So this sends a
    /// budgeted handful (under ten keys, each well under 1 KB), not a key per pool.
    /// The per-pool detail belongs in the soak report on disk; the crash needs the
    /// headline.</para>
    ///
    /// <para>Wired to <c>Application.lowMemory</c> automatically, because that is the
    /// last moment before the OS kills the app — the breadcrumbs captured there are
    /// the ones attached to the kill. Call <see cref="Capture"/> directly to refresh
    /// them at other moments (a level load, a return from the store).</para>
    /// </summary>
    public static class MemoryBreadcrumbs
    {
        /// <summary>Longest pool list the "busiest pools" key will hold. Keeps the value under the 1 KB cap.</summary>
        private const int MaxPoolsInKey = 6;

        private static readonly List<MemoryManager.PoolStat> StatBuffer = new();
        private static readonly NoOpSink NoOp = new();

        private static IBreadcrumbSink _sink = NoOp;

        /// <summary>
        /// The active sink. Set it once during boot to a crash-reporter adapter — see
        /// the Crashlytics sample. Setting null restores the no-op rather than throwing
        /// later from the low-memory handler, where a null-ref would replace the memory
        /// report with an unrelated crash.
        /// </summary>
        public static IBreadcrumbSink Sink
        {
            get => _sink;
            set => _sink = value ?? NoOp;
        }

        /// <summary>Whether a real sink is attached. False keeps <see cref="Capture"/> from doing work no one reads.</summary>
        public static bool HasSink => !ReferenceEquals(_sink, NoOp);

        /// <summary>Number of times <see cref="Application.lowMemory"/> has fired this session.</summary>
        public static int LowMemoryCount { get; private set; }

        /// <summary>Seconds-since-startup of the last low-memory signal, or -1.</summary>
        public static double LastLowMemoryTime { get; private set; } = -1d;

        /// <summary>
        /// Gathers the current memory state and pushes it to the sink. Cheap and
        /// allocation-conscious, but not on a hot path — call it on events, not every
        /// frame.
        /// </summary>
        public static void Capture()
        {
            if (!HasSink) return;

            _sink.Set("mtk_managed_mb", (Profiler.GetMonoUsedSizeLong() / (1024 * 1024)).ToString());
            _sink.Set("mtk_reserved_mb", (Profiler.GetTotalReservedMemoryLong() / (1024 * 1024)).ToString());
            _sink.Set("mtk_escapes", Migration.PoolBridge.UnknownInstanceCount.ToString());
            _sink.Set("mtk_lazy_pools", Migration.PoolBridge.LazyPoolCount.ToString());
            _sink.Set("mtk_scopes", ScopeSummary());
            _sink.Set("mtk_busiest_pools", BusiestPools());
            _sink.Set("mtk_low_memory_count", LowMemoryCount.ToString());
            _sink.Set("mtk_last_low_memory_s",
                LastLowMemoryTime >= 0 ? LastLowMemoryTime.ToString("0.0") : "never");
        }

        internal static void OnLowMemory()
        {
            LowMemoryCount++;
            LastLowMemoryTime = UnityEngine.Time.realtimeSinceStartupAsDouble;
            Capture();
        }

        private static string ScopeSummary()
        {
            IReadOnlyList<MemoryScope> scopes = MemoryManager.LiveScopes;
            StringBuilder sb = StringBuilderCache.Acquire(128);
            sb.Append(scopes.Count).Append(": ");
            for (int i = 0; i < scopes.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(scopes[i].Name);
                if (sb.Length > 400) { sb.Append(", ..."); break; } // stay clear of the 1 KB cap
            }

            return StringBuilderCache.GetStringAndRelease(sb);
        }

        private static string BusiestPools()
        {
            MemoryManager.GetPoolStats(StatBuffer);
            if (StatBuffer.Count == 0) return "none";

            // Selection of the top few by active count. A full sort would allocate,
            // and only a handful fit in the key anyway.
            StringBuilder sb = StringBuilderCache.Acquire(256);
            int shown = 0;
            var used = new bool[StatBuffer.Count];

            while (shown < MaxPoolsInKey)
            {
                int best = -1;
                for (int i = 0; i < StatBuffer.Count; i++)
                {
                    if (used[i]) continue;
                    if (best < 0 || StatBuffer[i].CountActive > StatBuffer[best].CountActive) best = i;
                }

                if (best < 0 || StatBuffer[best].CountActive == 0) break;

                used[best] = true;
                if (shown > 0) sb.Append(", ");
                sb.Append(StatBuffer[best].PrefabName).Append('=')
                    .Append(StatBuffer[best].CountActive).Append('/')
                    .Append(StatBuffer[best].CountAll);
                shown++;
            }

            if (shown == 0)
            {
                StringBuilderCache.Release(sb);
                return "all idle";
            }

            return StringBuilderCache.GetStringAndRelease(sb);
        }

        private sealed class NoOpSink : IBreadcrumbSink
        {
            public void Set(string key, string value) { }
        }
    }
}
