using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using MemoryToolkit.Buffers;
using UnityEngine.Profiling;

namespace MemoryToolkit.Diagnostics
{
    /// <summary>What happened, for the sparse event stream. See <see cref="MemoryRecorder"/>.</summary>
    public enum MemoryEventKind
    {
        ScopeCreated,
        ScopeDisposed,
        PoolCreated,
        PoolWarmedUp,
        PoolTrimmed,
        LowMemory,
        CollectFull,
    }

    /// <summary>A timestamped occurrence. <see cref="Value"/> is kind-specific (a count, or 0).</summary>
    public struct MemoryEvent
    {
        public double Time;
        public MemoryEventKind Kind;
        public string Label;
        public int Value;
    }

    /// <summary>One tick of a single pool's occupancy. <see cref="Alive"/> is false once the pool is gone.</summary>
    public struct PoolSample
    {
        public int Active;
        public int Inactive;
        public bool Alive;
    }

    /// <summary>One tick of process-wide state, shared as the timeline's x-axis.</summary>
    public struct GlobalSample
    {
        public double Time;
        public long ManagedUsedBytes;
        public int ScopeCount;
        public int FrameScratchUsedBytes;

        /// <summary>
        /// Instances that reached <c>PoolBridge.Return</c> owned by no toolkit pool
        /// during this interval — i.e. destroyed instead of pooled. The rate a pool
        /// exists to drive to zero.
        /// </summary>
        public int EscapeDelta;

        public int GetDelta;
        public int ReturnDelta;
        public int LazyPoolDelta;
    }

    /// <summary>
    /// Records pool and scope activity over time, so the Memory Inspector and the
    /// in-player overlay can show a history rather than a snapshot.
    ///
    /// <para><b>Why a time axis is the whole point.</b> The failures this package
    /// exists to prevent are transitions, not states. A pool registry that is wiped
    /// by a scene load, a scope that outlives the load which should have killed it,
    /// a pool that silently degrades into Instantiate/Destroy — none of these look
    /// wrong in the frame you are looking at. The snapshot taken afterwards is
    /// clean and empty. Only the transition is diagnostic, and only something that
    /// was already recording can show it to you.</para>
    ///
    /// <para>Two streams, because the two questions have different shapes:
    /// <b>events</b> are sparse and exact (this scope died at t=41.2), <b>samples</b>
    /// are dense and periodic (this pool held 12 active across the last minute).</para>
    ///
    /// <para>Disabled by default and compiled out entirely outside the editor and
    /// development builds — every recording entry point is
    /// <see cref="ConditionalAttribute"/>, so call sites in a release build vanish
    /// along with their argument evaluation. When enabled, a tick must not allocate:
    /// a diagnostic that produces garbage changes the thing it is measuring.</para>
    /// </summary>
    public static class MemoryRecorder
    {
        /// <summary>Seconds between samples. 4 Hz is enough to see a scene load, cheap enough to leave on.</summary>
        public static double SampleIntervalSeconds = 0.25;

        private static MemoryRing<MemoryEvent> _events;
        private static MemoryRing<GlobalSample> _global;
        private static readonly List<PoolSeries> Series = new();
        private static readonly Dictionary<(string Scope, string Prefab), int> SeriesIds = new();
        private static readonly List<MemoryManager.PoolStat> StatBuffer = new();

        private static double _nextSampleTime;
        private static int _lastEscapes, _lastGets, _lastReturns, _lastLazyPools;

        /// <summary>Whether the recorder is running. False until <see cref="Enable"/>.</summary>
        public static bool IsRecording { get; private set; }

        /// <summary>Time of the first retained sample, for the timeline's x-axis origin.</summary>
        public static double StartTime { get; private set; }

        /// <summary>
        /// Starts recording, sizing both buffers once. At the default 4 Hz,
        /// 480 samples is two minutes of history — long enough to contain a scene
        /// load and its aftermath, which is the span that matters.
        /// </summary>
        public static void Enable(int sampleCapacity = 480, int eventCapacity = 128)
        {
            _events = new MemoryRing<MemoryEvent>(eventCapacity);
            _global = new MemoryRing<GlobalSample>(sampleCapacity);
            Series.Clear();
            SeriesIds.Clear();
            _lastEscapes = _lastGets = _lastReturns = _lastLazyPools = 0;
            StartTime = Now;
            _nextSampleTime = 0;
            IsRecording = true;
        }

        /// <summary>Stops recording. Buffers are kept so the view stays readable.</summary>
        public static void Disable() => IsRecording = false;

        /// <summary>Drops all history and restarts the clock. Use between measured runs.</summary>
        public static void Clear()
        {
            _events?.Clear();
            _global?.Clear();
            for (int i = 0; i < Series.Count; i++)
                Series[i].Samples.Clear();
            StartTime = Now;
        }

        // ---- Recording entry points -------------------------------------------------
        // Conditional: in a release player these calls are removed by the compiler,
        // arguments included, so instrumenting hot paths costs nothing there.

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        internal static void RecordEvent(MemoryEventKind kind, string label, int value = 0)
        {
            if (!IsRecording) return;
            _events.Add(new MemoryEvent { Time = Now, Kind = kind, Label = label, Value = value });
        }

        /// <summary>
        /// Samples if the interval has elapsed. Driven from the runner's LateUpdate
        /// beside the frame-arena reset rather than by a second driver object.
        /// </summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        internal static void Tick()
        {
            if (!IsRecording) return;

            double now = Now;
            if (now < _nextSampleTime) return;
            _nextSampleTime = now + SampleIntervalSeconds;

            SamplePools();
            SampleGlobal(now);
        }

        private static void SamplePools()
        {
            for (int i = 0; i < Series.Count; i++)
                Series[i].SeenThisTick = false;

            // Reused list; PoolStat holds cached strings, so this does not allocate
            // once every row has been seen at least once.
            MemoryManager.GetPoolStats(StatBuffer);
            for (int i = 0; i < StatBuffer.Count; i++)
            {
                MemoryManager.PoolStat stat = StatBuffer[i];
                PoolSeries series = GetOrCreateSeries(stat);
                series.SeenThisTick = true;
                series.WasWarmedUp = stat.WasWarmedUp;
                series.Samples.Add(new PoolSample
                {
                    Active = stat.CountActive,
                    Inactive = stat.CountInactive,
                    Alive = true,
                });
            }

            // A pool that stopped reporting still gets a sample, so its row ends at a
            // readable point on the shared axis instead of just disappearing.
            for (int i = 0; i < Series.Count; i++)
            {
                if (Series[i].SeenThisTick) continue;
                Series[i].Samples.Add(default);
            }
        }

        private static void SampleGlobal(double now)
        {
            int escapes = Migration.PoolBridge.UnknownInstanceCount;
            int gets = Migration.PoolBridge.GetCount;
            int returns = Migration.PoolBridge.ReturnCount;
            int lazyPools = Migration.PoolBridge.LazyPoolCount;

            FrameAllocator scratch = MemoryManager.FrameScratchOrNull;

            _global.Add(new GlobalSample
            {
                Time = now,
                ManagedUsedBytes = Profiler.GetMonoUsedSizeLong(),
                ScopeCount = MemoryManager.LiveScopes.Count,
                FrameScratchUsedBytes = scratch?.PeakUsedBytes ?? 0,
                EscapeDelta = escapes - _lastEscapes,
                GetDelta = gets - _lastGets,
                ReturnDelta = returns - _lastReturns,
                LazyPoolDelta = lazyPools - _lastLazyPools,
            });

            _lastEscapes = escapes;
            _lastGets = gets;
            _lastReturns = returns;
            _lastLazyPools = lazyPools;
        }

        private static PoolSeries GetOrCreateSeries(in MemoryManager.PoolStat stat)
        {
            var key = (stat.ScopeName, stat.PrefabName);
            if (SeriesIds.TryGetValue(key, out int id))
                return Series[id];

            // Keyed on names, not on the pool or prefab object: a pool disposed and
            // recreated for the same prefab is the same row to a reader, and a key
            // derived from a loaded asset goes stale when Addressables releases it.
            var series = new PoolSeries(stat.ScopeName, stat.PrefabName, _global.Capacity);
            SeriesIds.Add(key, Series.Count);
            Series.Add(series);
            return series;
        }

        // ---- Read access for viewers ------------------------------------------------

        internal static MemoryRing<GlobalSample> GlobalSamples => _global;
        internal static MemoryRing<MemoryEvent> Events => _events;
        internal static IReadOnlyList<PoolSeries> PoolSeriesList => Series;

        /// <summary>
        /// Plain-text report of the current buffers. For device logs and CI, where
        /// there is no window to look at — the recorder's data is worth having even
        /// when nothing can draw it.
        /// </summary>
        public static string Dump()
        {
            StringBuilder sb = StringBuilderCache.Acquire(1024);
            sb.Append("[MemoryToolkit] recorder dump — recording=").Append(IsRecording).AppendLine();

            if (_global != null && _global.Count > 0)
            {
                ref GlobalSample last = ref _global[_global.Count - 1];
                int escapes = 0;
                for (int i = 0; i < _global.Count; i++)
                    escapes += _global[i].EscapeDelta;

                sb.Append("managed used: ").Append(last.ManagedUsedBytes / 1024).AppendLine(" KB");
                sb.Append("live scopes: ").Append(last.ScopeCount).AppendLine();
                sb.Append("escapes in window: ").Append(escapes).AppendLine();
            }

            sb.AppendLine("pools (scope/prefab: active/inactive, warmed):");
            for (int i = 0; i < Series.Count; i++)
            {
                PoolSeries series = Series[i];
                if (series.Samples.Count == 0) continue;
                ref PoolSample s = ref series.Samples[series.Samples.Count - 1];
                sb.Append("  ").Append(series.ScopeName).Append('/').Append(series.PrefabName)
                    .Append(": ").Append(s.Active).Append('/').Append(s.Inactive)
                    .Append(series.WasWarmedUp ? ", warmed" : ", NOT warmed")
                    .Append(", peak active ").Append(series.PeakActive)
                    .AppendLine(series.Alive ? "" : ", DEAD");
            }

            if (_events != null)
            {
                sb.AppendLine("events:");
                for (int i = 0; i < _events.Count; i++)
                {
                    ref MemoryEvent e = ref _events[i];
                    sb.Append("  t=").Append((e.Time - StartTime).ToString("0.00"))
                        .Append(' ').Append(e.Kind.ToString())
                        .Append(' ').Append(e.Label);
                    if (e.Value != 0) sb.Append(" (").Append(e.Value).Append(')');
                    sb.AppendLine();
                }
            }

            return StringBuilderCache.GetStringAndRelease(sb);
        }

        private static double Now => UnityEngine.Time.realtimeSinceStartupAsDouble;
    }

    /// <summary>One pool's history. Identified by name so the row survives the pool object.</summary>
    internal sealed class PoolSeries
    {
        internal readonly string ScopeName;
        internal readonly string PrefabName;

        /// <summary>Display key, built once — this is read every refresh by the viewer.</summary>
        internal readonly string Name;

        internal readonly MemoryRing<PoolSample> Samples;
        internal bool SeenThisTick;
        internal bool WasWarmedUp;

        internal PoolSeries(string scopeName, string prefabName, int capacity)
        {
            ScopeName = scopeName;
            PrefabName = prefabName;
            Name = scopeName + "/" + prefabName;
            Samples = new MemoryRing<PoolSample>(capacity);
        }

        internal bool Alive => Samples.Count > 0 && Samples[Samples.Count - 1].Alive;

        /// <summary>
        /// High-water mark of active instances across the retained window.
        /// <b>This is the warm-up count.</b> The instantaneous active count that a
        /// snapshot shows cannot size a pool; the peak can.
        /// </summary>
        internal int PeakActive
        {
            get
            {
                int peak = 0;
                for (int i = 0; i < Samples.Count; i++)
                {
                    int active = Samples[i].Active;
                    if (active > peak) peak = active;
                }
                return peak;
            }
        }
    }
}
