using MemoryToolkit.Pooling;
using UnityEngine;

namespace MemoryToolkit.Samples
{
    /// <summary>
    /// A pooled projectile: flies forward, then releases itself back to its
    /// pool after <see cref="lifetime"/> seconds. Note there is no Destroy call
    /// anywhere — the instance is recycled, never garbage.
    /// </summary>
    public sealed class PooledProjectile : MonoBehaviour, IPoolable
    {
        [SerializeField] private float speed = 20f;
        [SerializeField] private float lifetime = 3f;

        private float _age;
        private PooledInstance _handle;

        private void Awake() => _handle = GetComponent<PooledInstance>();

        public void OnTakenFromPool()
        {
            // Reset per-use state on take, so the instance never carries state
            // from its previous life.
            _age = 0f;
            if (_handle == null) _handle = GetComponent<PooledInstance>();
        }

        public void OnReturnedToPool()
        {
            // Stop anything that must not keep running while pooled
            // (trail renderers, audio, coroutines, tweens...).
        }

        private void Update()
        {
            transform.position += transform.forward * (speed * Time.deltaTime);
            _age += Time.deltaTime;
            if (_age >= lifetime)
                _handle.Release();
        }
    }
}
