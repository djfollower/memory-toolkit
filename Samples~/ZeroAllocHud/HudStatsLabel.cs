using MemoryToolkit.Buffers;
using UnityEngine;

namespace MemoryToolkit.Samples.ZeroAllocHud
{
    /// <summary>
    /// SCENARIO: HUD text that updates constantly without shredding the heap.
    ///
    /// The classic mistake is `label.text = "FPS: " + fps + " Score: " + score`
    /// every frame — several intermediate strings per frame, thousands of tiny
    /// dead objects per minute, textbook Gen0 churn. This label instead:
    ///
    /// 1. Assembles text in a cached StringBuilder (zero intermediate garbage).
    /// 2. Rebuilds ONLY when a displayed value actually changed — the single
    ///    final string is unavoidable with Unity's string-typed text APIs, so
    ///    the fix is to produce it rarely, not per frame. (With TextMeshPro,
    ///    go further: `tmp.SetText(sb)` accepts the builder directly and skips
    ///    even that final string.)
    ///
    /// Uses the built-in TextMesh so the sample has no package dependencies;
    /// the pattern is identical for UGUI Text or TMP_Text.
    /// </summary>
    [RequireComponent(typeof(TextMesh))]
    public sealed class HudStatsLabel : MonoBehaviour
    {
        public int Score { get; set; }

        private TextMesh _label;
        private int _shownFps = -1;
        private int _shownScore = -1;

        private void Awake() => _label = GetComponent<TextMesh>();

        private void Update()
        {
            Score += (int)(Time.deltaTime * 100f); // demo driver; replace with real game state

            int fps = (int)(1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f));
            if (fps == _shownFps && Score == _shownScore)
                return; // nothing changed: zero work, zero allocation

            _shownFps = fps;
            _shownScore = Score;

            var sb = StringBuilderCache.Acquire();
            sb.Append("FPS ").Append(fps).Append('\n')
              .Append("Score ").Append(Score);
            _label.text = StringBuilderCache.GetStringAndRelease(sb);
        }
    }
}
