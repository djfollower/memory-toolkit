using UnityEngine;

namespace MemoryToolkit.Pooling
{
    /// <summary>
    /// A reference to a pooled component that can tell you whether it still
    /// points at the same *occupant* of that instance.
    ///
    /// Pooling breaks the assumption that a non-null reference is still yours.
    /// An instance released and re-taken is alive, non-null, and belongs to a
    /// different caller — so the usual guard fails silently:
    ///
    /// <code>
    /// var enemy = pool.Get&lt;Enemy&gt;();
    /// await LoadLoadoutAsync();
    /// if (enemy != null) enemy.Equip(loadout); // passes even if `enemy` was recycled
    /// </code>
    ///
    /// Capture a <see cref="PooledRef{T}"/> before the await and check it after:
    ///
    /// <code>
    /// PooledRef&lt;Enemy&gt; enemy = PooledRef.To(pool.Get&lt;Enemy&gt;());
    /// await LoadLoadoutAsync();
    /// if (enemy.TryGet(out Enemy e)) e.Equip(loadout);
    /// </code>
    ///
    /// Non-pooled components are supported and are alive while simply non-null,
    /// so call sites do not need to know whether their target came from a pool.
    /// </summary>
    public readonly struct PooledRef<T> where T : Component
    {
        private readonly T _target;
        private readonly PooledInstance _handle;
        private readonly uint _generation;

        internal PooledRef(T target, PooledInstance handle)
        {
            _target = target;
            _handle = handle;
            _generation = handle != null ? handle.Generation : 0u;
        }

        /// <summary>
        /// True when the target still exists and has not been recycled since
        /// capture. False for a destroyed object, a returned-to-pool instance,
        /// or one already handed to a new owner.
        /// </summary>
        public bool IsAlive
        {
            get
            {
                if (_target == null) return false;
                if (_handle == null) return true; // not pooled: liveness is just non-null
                return !_handle.IsInPool && _handle.Generation == _generation;
            }
        }

        /// <summary>Yields the target only when <see cref="IsAlive"/>.</summary>
        public bool TryGet(out T value)
        {
            if (IsAlive)
            {
                value = _target;
                return true;
            }

            value = null;
            return false;
        }
    }

    /// <summary>Factory for <see cref="PooledRef{T}"/> so the type argument can be inferred.</summary>
    public static class PooledRef
    {
        /// <summary>Captures <paramref name="component"/> together with its current pool generation.</summary>
        public static PooledRef<T> To<T>(T component) where T : Component
        {
            if (component == null)
                return default;

            // Resolved through the cache when the instance is pooled; a plain
            // GetComponent miss (null) simply means "not pooled".
            var handle = component.GetComponent<PooledInstance>();
            return new PooledRef<T>(component, handle);
        }
    }
}
