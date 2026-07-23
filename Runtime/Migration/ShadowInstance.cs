using UnityEngine;

namespace MemoryToolkit.Migration
{
    /// <summary>
    /// Marks an instance created by <see cref="PoolShadow"/> while
    /// <see cref="PoolBridge.Mode"/> is <see cref="PoolBridgeMode.Observe"/>, so a
    /// later <see cref="PoolBridge.Return"/> can attribute it back to the prefab it
    /// came from.
    ///
    /// <para>Deliberately a component rather than an entry in a
    /// <c>Dictionary&lt;GameObject, int&gt;</c>, for the same reason
    /// <see cref="Pooling.PooledInstance"/> is: identity that travels on the instance
    /// survives everything a lookup table does not — a scene load, a cleared
    /// registry, an asset released and reloaded. A shadow run is measuring a
    /// session long enough for exactly those things to happen.</para>
    ///
    /// <para>Adding a component per instance is an allocation, which is why Observe
    /// mode is refused outside the editor and development builds. It measures what
    /// pooling would save; it is not itself an optimisation.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")] // hidden from the Add Component menu; shadow-managed only
    public sealed class ShadowInstance : MonoBehaviour
    {
        /// <summary>Index into <see cref="PoolShadow.Entries"/>.</summary>
        internal int EntryId = -1;

        /// <summary>
        /// Set on the first return. A second return is counted rather than acted on,
        /// mirroring <see cref="Pooling.GameObjectPool.DoubleReleaseCount"/> — double
        /// release is a real defect in incumbent call sites, and a shadow run is the
        /// cheapest place to discover it.
        /// </summary>
        internal bool Returned;
    }
}
