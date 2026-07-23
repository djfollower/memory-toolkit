using MemoryToolkit;
using MemoryToolkit.Buffers;
using MemoryToolkit.Pooling;
using Unity.Collections;
using UnityEngine;

namespace MemoryToolkit.Samples.MatchScope
{
    /// <summary>
    /// SCENARIO: a lifetime shorter than the scene — a round, a match, a wave.
    ///
    /// The arena scene stays loaded across rounds, so a scene scope is the
    /// wrong tier: round memory would accumulate. Instead each round gets a
    /// manual scope. Everything the round needs — the unit pool, a native
    /// arena for AI scratch, a native score table — is created in or
    /// registered with that scope, and one Dispose at round end frees it all.
    ///
    /// This demo auto-cycles rounds on a timer so it runs without any input
    /// setup; watch the "Match" scope appear and vanish in the Memory Inspector
    /// window (Window &gt; Analysis &gt; Memory Toolkit Inspector).
    /// </summary>
    public sealed class MatchController : MonoBehaviour
    {
        [SerializeField] private GameObject unitPrefab;
        [SerializeField] private int unitsPerMatch = 12;
        [SerializeField] private float matchDurationSeconds = 10f;
        [SerializeField] private float intermissionSeconds = 3f;

        private MemoryScope _matchScope;
        private GameObjectPool _unitPool;
        private FrameAllocator _aiScratch;
        private NativeArray<int> _scores;
        private float _timer;
        private int _round;

        private void Start() => _timer = intermissionSeconds;

        private void Update()
        {
            _timer -= Time.deltaTime;
            if (_timer > 0f) return;

            if (_matchScope == null)
                StartMatch();
            else
                EndMatch();
        }

        private void StartMatch()
        {
            _round++;
            _matchScope = MemoryManager.CreateScope($"Match {_round}");

            // Round-lifetime allocations, all owned by the scope:
            _unitPool = _matchScope.GetPool(unitPrefab, unitsPerMatch, unitsPerMatch * 2);
            _unitPool.Warmup(unitsPerMatch);
            _aiScratch = _matchScope.CreateAllocator(64 * 1024);
            _scores = _matchScope.Register(new DisposableScores(unitsPerMatch)).Values;

            for (int i = 0; i < unitsPerMatch; i++)
            {
                Vector2 spot = Random.insideUnitCircle * 8f;
                _unitPool.Get(new Vector3(spot.x, 0f, spot.y), Quaternion.identity);
            }

            _timer = matchDurationSeconds;
        }

        private void EndMatch()
        {
            // One call. Units are pooled away and destroyed with the pool, the
            // AI arena's native block is freed, the score table is disposed —
            // in reverse registration order, deterministically, this frame.
            _matchScope.Dispose();
            _matchScope = null;
            _unitPool = null;
            _aiScratch = null;

            _timer = intermissionSeconds;
        }

        private void LateUpdate()
        {
            if (_aiScratch == null) return;

            // Per-tick AI scratch from the round's own arena: for example,
            // candidate target positions evaluated and discarded every frame.
            NativeArray<Vector3> candidates = _aiScratch.Allocate<Vector3>(unitsPerMatch);
            for (int i = 0; i < candidates.Length; i++)
            {
                candidates[i] = new Vector3(i, 0f, _round);
                _scores[i]++; // round-lifetime data lives in the scope-owned table
            }

            // A round arena is reset by its owner, not by MemoryManager:
            _aiScratch.Reset();
        }

        /// <summary>
        /// Wraps round-lifetime native data so <see cref="MemoryScope.Register"/>
        /// can own it. Prefer registering a small owner class like this over
        /// registering raw native containers one by one.
        /// </summary>
        private sealed class DisposableScores : System.IDisposable
        {
            public NativeArray<int> Values;
            public DisposableScores(int count) => Values = new NativeArray<int>(count, Allocator.Persistent);
            public void Dispose() => Values.Dispose();
        }
    }
}
