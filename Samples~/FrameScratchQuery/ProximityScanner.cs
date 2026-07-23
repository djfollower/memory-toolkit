using MemoryToolkit;
using Unity.Collections;
using UnityEngine;

namespace MemoryToolkit.Samples.FrameScratchQuery
{
    /// <summary>
    /// SCENARIO: per-frame derived data with zero managed allocation.
    ///
    /// Every frame this scans for nearby colliders and computes a threat
    /// weight per hit — the kind of transient working set (query results,
    /// candidate lists, staging buffers) that is usually built with
    /// `new float[count]` or a temporary List and becomes per-frame garbage.
    ///
    /// The two-part pattern:
    /// 1. Fixed-capacity persistent buffer for the physics query itself
    ///    (`OverlapSphereNonAlloc` requires a managed array) — allocated once
    ///    in Awake, sized to a hard gameplay cap.
    /// 2. Everything derived from it goes into `MemoryManager.FrameScratch`,
    ///    sized per-frame to the actual hit count. No clearing, no pooling
    ///    bookkeeping — the arena resets in LateUpdate.
    ///
    /// Verify in the Profiler: this component's Update shows 0 B GC Alloc.
    /// </summary>
    public sealed class ProximityScanner : MonoBehaviour
    {
        [SerializeField] private float radius = 15f;
        [SerializeField, Min(1)] private int maxHits = 64;
        [SerializeField] private LayerMask layers = ~0;

        /// <summary>Closest hit this frame; null when nothing is in range.</summary>
        public Collider Nearest { get; private set; }

        private Collider[] _hits; // persistent query buffer, never resized

        private void Awake() => _hits = new Collider[maxHits];

        private void Update()
        {
            int count = Physics.OverlapSphereNonAlloc(transform.position, radius, _hits, layers);
            Nearest = null;
            if (count == 0) return;

            // Transient, exactly-sized, valid this frame only:
            NativeArray<float> threat = MemoryManager.FrameScratch.Allocate<float>(count);

            float best = float.MaxValue;
            Vector3 origin = transform.position;
            for (int i = 0; i < count; i++)
            {
                float sqDist = (_hits[i].transform.position - origin).sqrMagnitude;
                threat[i] = 1f / (1f + sqDist); // closer = more threatening
                if (sqDist < best)
                {
                    best = sqDist;
                    Nearest = _hits[i];
                }

                _hits[i] = null; // drop refs so pooled/destroyed colliders aren't kept alive
            }

            // `threat` would feed targeting/steering here. It is NOT stored:
            // after this frame the arena recycles the bytes.
            if (Nearest != null)
                Debug.DrawLine(origin, Nearest.transform.position, Color.red);
        }
    }
}
