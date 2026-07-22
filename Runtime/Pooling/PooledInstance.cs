using System;
using UnityEngine;

namespace MemoryToolkit.Pooling
{
    /// <summary>
    /// Attached automatically to every instance created by <see cref="GameObjectPool"/>.
    /// Remembers the owning pool so callers can release an instance without
    /// keeping a reference to the pool, and guards against double release.
    ///
    /// Also carries the two things pooling needs that a bare GameObject cannot
    /// provide: a per-instance component cache (so the hot path does not pay
    /// <c>GetComponent</c> on every <see cref="GameObjectPool.Get{T}()"/>) and a
    /// <see cref="Generation"/> counter that makes stale references detectable
    /// (see <see cref="PooledRef{T}"/>).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")] // hidden from the Add Component menu; pool-managed only
    public sealed class PooledInstance : MonoBehaviour
    {
        internal GameObjectPool Owner { get; set; }
        internal bool IsInPool { get; set; }

        /// <summary>
        /// True while this instance is sitting in its pool rather than in use.
        /// O(1) — it is a field on the instance, not a search of the free list.
        /// Call sites should not need this (<see cref="Release"/> is already
        /// double-release safe); it exists for diagnostics and for migration
        /// shims that must answer an incumbent pool's equivalent query.
        /// </summary>
        public bool IsPooled => IsInPool;

        /// <summary>
        /// Incremented every time this instance is returned to its pool. A
        /// reference captured during one use is stale once this changes — the
        /// object is alive and non-null, but it now belongs to someone else.
        /// This is what a null check cannot tell you.
        /// </summary>
        public uint Generation { get; private set; }

        // Per-instance component cache. Linear scan over a handful of entries
        // beats a dictionary at this size and allocates nothing after warm-up.
        private Type[] _cachedTypes;
        private Component[] _cachedComponents;
        private int _cacheCount;

        /// <summary>
        /// Returns this instance to the pool it came from.
        /// Safe to call multiple times; only the first call has an effect.
        ///
        /// <para>The already-pooled case is deliberately delegated to the pool
        /// rather than short-circuited here, so that one place owns the
        /// idempotency rule and repeat releases land in
        /// <see cref="GameObjectPool.DoubleReleaseCount"/> wherever they come
        /// from.</para>
        /// </summary>
        public void Release()
        {
            if (Owner == null)
                return;
            Owner.Release(gameObject);
        }

        internal void BumpGeneration() => Generation++;

        /// <summary>
        /// <c>GetComponent&lt;T&gt;</c> resolved once per instance per type, then
        /// served from the cache. Returns null when the prefab has no such
        /// component; callers that require one should throw.
        /// </summary>
        internal T GetCached<T>() where T : Component
        {
            Type type = typeof(T);
            for (int i = 0; i < _cacheCount; i++)
            {
                if (_cachedTypes[i] == type)
                    return (T)_cachedComponents[i];
            }

            var component = GetComponent<T>();
            if (component != null)
                AddToCache(type, component);
            return component;
        }

        private void AddToCache(Type type, Component component)
        {
            if (_cachedTypes == null)
            {
                _cachedTypes = new Type[4];
                _cachedComponents = new Component[4];
            }
            else if (_cacheCount == _cachedTypes.Length)
            {
                Array.Resize(ref _cachedTypes, _cacheCount * 2);
                Array.Resize(ref _cachedComponents, _cacheCount * 2);
            }

            _cachedTypes[_cacheCount] = type;
            _cachedComponents[_cacheCount] = component;
            _cacheCount++;
        }
    }
}
