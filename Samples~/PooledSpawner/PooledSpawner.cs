using MemoryToolkit;
using MemoryToolkit.Pooling;
using UnityEngine;

namespace MemoryToolkit.Samples
{
    /// <summary>
    /// Fires pooled projectiles at a fixed rate. Assign a prefab that has (or
    /// will receive) a <see cref="PooledProjectile"/> component, enter play
    /// mode, and open Window &gt; Analysis &gt; Memory Toolkit Inspector to
    /// watch instances recycle: CountAll stops growing once the pool reaches
    /// steady state, meaning zero Instantiate/Destroy churn.
    /// </summary>
    public sealed class PooledSpawner : MonoBehaviour
    {
        [SerializeField] private GameObject projectilePrefab;
        [SerializeField] private float shotsPerSecond = 10f;
        [SerializeField] private int warmupCount = 32;

        [Tooltip("When on, the pool lives in a scene scope and is destroyed when this scene unloads. When off, it lives in the Permanent scope for the whole session.")]
        [SerializeField] private bool useSceneScope = true;

        private GameObjectPool _pool;
        private float _cooldown;

        private void Start()
        {
            // Resolve and warm the pool once, up front — never per shot.
            // Scene scope: this level's instances are freed in one step on
            // scene unload. Permanent: they persist across levels.
            _pool = useSceneScope
                ? MemoryManager.CreateSceneScope(gameObject.scene).GetPool(projectilePrefab)
                : MemoryManager.GetPool(projectilePrefab);
            _pool.Warmup(warmupCount);
        }

        private void Update()
        {
            _cooldown -= Time.deltaTime;
            if (_cooldown > 0f) return;
            _cooldown = 1f / shotsPerSecond;

            _pool.Get(transform.position, transform.rotation);
        }
    }
}
