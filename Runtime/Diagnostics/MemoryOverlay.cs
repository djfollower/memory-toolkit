using System.Diagnostics;
using System.Text;
using MemoryToolkit.Buffers;
using UnityEngine;
using UnityEngine.Scripting;

namespace MemoryToolkit.Diagnostics
{
    /// <summary>
    /// On-screen view of <see cref="MemoryRecorder"/> for a running player.
    ///
    /// <para>The Editor window can only show you what happens in the Editor, and
    /// the memory failures that matter happen on a four-year-old phone with a
    /// quarter of the RAM, after twenty minutes of play, on a build nobody can
    /// attach a profiler to. This is deliberately the crudest possible renderer —
    /// <c>OnGUI</c> on a hidden object, no canvas, no prefab, no uGUI dependency —
    /// so that turning it on is never a project-setup task.</para>
    ///
    /// <para><b>This stays IMGUI on purpose, even though the Memory Inspector window
    /// is UI Toolkit.</b> They are not the same job. The Inspector renders trends you
    /// study, so stroked charts earn their cost; this is four numbers and two strip
    /// charts you glance at mid-play. Runtime UI Toolkit would require a
    /// <c>PanelSettings</c> asset and a theme stylesheet wired up before anything
    /// draws, and that trade is backwards here: the whole value of this overlay is
    /// that <see cref="Show"/> is the entire integration, on any project, with no
    /// asset to author and nothing to add to a scene. Do not "unify" the two.</para>
    ///
    /// <para>Off by default; <see cref="Show"/> compiles away outside the editor
    /// and development builds.</para>
    /// </summary>
    public static class MemoryOverlay
    {
        /// <summary>Whether the overlay is currently on screen.</summary>
        public static bool IsVisible { get; private set; }

        /// <summary>Screen corner offset, in pixels.</summary>
        public static Vector2 Origin = new(10f, 10f);

        private static MemoryOverlayRunner _runner;

        /// <summary>
        /// Shows the overlay, starting the recorder if it is not already running.
        /// Call it from a debug menu or a cheat key.
        /// </summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Show()
        {
            if (!MemoryRecorder.IsRecording)
                MemoryRecorder.Enable();

            if (_runner == null)
            {
                var go = new GameObject("[MemoryToolkit] Overlay")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                Object.DontDestroyOnLoad(go);
                _runner = go.AddComponent<MemoryOverlayRunner>();
            }

            IsVisible = true;
        }

        /// <summary>Hides the overlay. The recorder keeps running.</summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Hide() => IsVisible = false;

        /// <summary>Shows or hides. Convenient to bind to a key.</summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Toggle()
        {
            if (IsVisible) Hide();
            else Show();
        }
    }

    [Preserve]
    [AddComponentMenu("")]
    internal sealed class MemoryOverlayRunner : MonoBehaviour
    {
        private const int Width = 300;
        private const int ChartHeight = 40;
        private const int MaxColumns = 120;
        private const int MaxPoolRows = 8;

        // Comfortably above the 12pt font: at 14 the rows touched each other.
        private const float LineHeight = 16f;

        private Texture2D _pixel;
        private GUIStyle _style;

        private void OnGUI()
        {
            if (!MemoryOverlay.IsVisible) return;

            EnsureResources();

            var area = new Rect(MemoryOverlay.Origin.x, MemoryOverlay.Origin.y, Width, 0f);
            MemoryRing<GlobalSample> samples = MemoryRecorder.GlobalSamples;
            if (samples == null || samples.Count == 0)
            {
                Draw(ref area, 18f, "[MemoryToolkit] recorder has no samples yet");
                return;
            }

            ref GlobalSample last = ref samples[samples.Count - 1];

            // Height is computed rather than guessed: IMGUI has no layout pass, and
            // a fixed backdrop clips the pool rows as soon as a project has more
            // pools than the number someone happened to test with.
            int poolRows = Mathf.Min(MemoryRecorder.PoolSeriesList.Count, MaxPoolRows);
            float height = LineHeight * 2f + (ChartHeight + 4f) * 2f + 4f
                           + LineHeight + poolRows * LineHeight + 6f;

            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(area.x - 4f, area.y - 4f, Width + 8f, height), _pixel);
            GUI.color = Color.white;

            StringBuilder sb = StringBuilderCache.Acquire(128);
            sb.Append("managed ").Append(last.ManagedUsedBytes / (1024 * 1024)).Append(" MB");
            sb.Append("   scopes ").Append(last.ScopeCount);
            Draw(ref area, LineHeight, StringBuilderCache.GetStringAndRelease(sb));

            DrawChart(ref area, samples, ChartMetric.ManagedBytes, Color.cyan);

            // Escapes are the one number worth a colour change: non-zero means
            // instances are being destroyed instead of pooled, which costs more
            // than not pooling at all.
            int escapes = 0, gets = 0, returns = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                escapes += samples[i].EscapeDelta;
                gets += samples[i].GetDelta;
                returns += samples[i].ReturnDelta;
            }

            // Labelled as bridge counters on purpose: they only move when calls come
            // through PoolBridge, so a project using pools directly sees zeros here
            // beside plainly busy pools. Unlabelled, that reads as a broken overlay.
            sb = StringBuilderCache.Acquire(128);
            sb.Append("bridge  get ").Append(gets).Append("  return ").Append(returns)
                .Append("  escaped ").Append(escapes);
            GUI.color = escapes > 0 ? Color.red : Color.green;
            Draw(ref area, LineHeight, StringBuilderCache.GetStringAndRelease(sb));
            GUI.color = Color.white;

            DrawChart(ref area, samples, ChartMetric.Escapes, Color.red);

            area.y += 4f;
            Draw(ref area, LineHeight, "pools (active/inactive, peak)");
            var series = MemoryRecorder.PoolSeriesList;
            for (int i = 0; i < series.Count && i < MaxPoolRows; i++)
            {
                PoolSeries s = series[i];
                if (s.Samples.Count == 0) continue;
                ref PoolSample sample = ref s.Samples[s.Samples.Count - 1];

                sb = StringBuilderCache.Acquire(96);
                sb.Append(s.PrefabName).Append(": ").Append(sample.Active).Append('/')
                    .Append(sample.Inactive).Append("  peak ").Append(s.PeakActive);
                if (!s.WasWarmedUp) sb.Append("  NOT WARMED");
                GUI.color = s.Alive ? Color.white : Color.gray;
                Draw(ref area, LineHeight, StringBuilderCache.GetStringAndRelease(sb));
            }
            GUI.color = Color.white;
        }

        private enum ChartMetric { ManagedBytes, Escapes }

        private void DrawChart(ref Rect area, MemoryRing<GlobalSample> samples, ChartMetric metric, Color color)
        {
            var rect = new Rect(area.x, area.y, Width, ChartHeight);
            area.y += ChartHeight + 4f;

            int count = Mathf.Min(samples.Count, MaxColumns);
            int first = samples.Count - count;

            double max = 1d;
            for (int i = first; i < samples.Count; i++)
                max = System.Math.Max(max, Value(samples[i], metric));

            float columnWidth = rect.width / count;
            GUI.color = color;
            for (int i = 0; i < count; i++)
            {
                float normalized = (float)(Value(samples[first + i], metric) / max);
                float height = Mathf.Max(1f, normalized * rect.height);
                GUI.DrawTexture(new Rect(rect.x + i * columnWidth, rect.yMax - height,
                    Mathf.Max(1f, columnWidth - 1f), height), _pixel);
            }
            GUI.color = Color.white;
        }

        private static double Value(in GlobalSample sample, ChartMetric metric)
            => metric == ChartMetric.ManagedBytes ? sample.ManagedUsedBytes : sample.EscapeDelta;

        private void Draw(ref Rect area, float height, string text)
        {
            area.height = height;
            GUI.Label(area, text, _style);
            area.y += height;
        }

        private void EnsureResources()
        {
            if (_pixel == null)
            {
                _pixel = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
                _pixel.SetPixel(0, 0, Color.white);
                _pixel.Apply();
            }

            _style ??= new GUIStyle(GUI.skin.label) { fontSize = 12, richText = false };
        }

        private void OnDestroy()
        {
            if (_pixel != null) Destroy(_pixel);
        }
    }
}
