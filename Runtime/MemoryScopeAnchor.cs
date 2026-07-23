using UnityEngine;

namespace MemoryToolkit
{
    /// <summary>
    /// Disposes a <see cref="MemoryScope"/> when the GameObject it is attached to is
    /// destroyed. Added by <see cref="MemoryScopeExtensions.AttachTo"/>; not something
    /// to add by hand.
    ///
    /// <para>This is the escape hatch for projects whose lifetimes are not Unity's.
    /// <see cref="MemoryManager.CreateSceneScope"/> hooks <c>sceneUnloaded</c>, which
    /// is correct only when scenes are how the project ends things — and in a lot of
    /// studios they are not. Addressables scene loads, a bespoke flow manager, an
    /// additive UI stack, a match that ends without a scene change: each of those has
    /// a GameObject whose destruction *is* the end of the lifetime, and anchoring to
    /// it is more honest than asking the project to reorganise around scenes.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")] // attached programmatically; nothing to configure
    // Without this, MonoBehaviour messages do not run outside play mode, so an
    // anchor attached from an editor tool would never fire and the scope would leak
    // with no symptom. The component has one callback and no update cost, so making
    // it behave identically in both modes is cheaper than documenting the trap.
    [ExecuteAlways]
    internal sealed class MemoryScopeAnchor : MonoBehaviour
    {
        internal MemoryScope Scope;

        private void OnDestroy()
        {
            // Null-safe on purpose: domain reload and editor teardown can run this
            // with a scope that is already gone, and a memory tool that throws
            // during shutdown is worse than one that does nothing.
            Scope?.Dispose();
            Scope = null;
        }
    }
}
