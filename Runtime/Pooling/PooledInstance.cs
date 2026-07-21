using UnityEngine;

namespace MemoryToolkit.Pooling
{
    /// <summary>
    /// Attached automatically to every instance created by <see cref="GameObjectPool"/>.
    /// Remembers the owning pool so callers can release an instance without
    /// keeping a reference to the pool, and guards against double release.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")] // hidden from the Add Component menu; pool-managed only
    public sealed class PooledInstance : MonoBehaviour
    {
        internal GameObjectPool Owner { get; set; }
        internal bool IsInPool { get; set; }

        /// <summary>
        /// Returns this instance to the pool it came from.
        /// Safe to call multiple times; only the first call has an effect.
        /// </summary>
        public void Release()
        {
            if (IsInPool || Owner == null)
                return;
            Owner.Release(gameObject);
        }
    }
}
