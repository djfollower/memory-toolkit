using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MemoryToolkit
{
    /// <summary>
    /// Ways to bind a scope's disposal to something other than a scene unload.
    ///
    /// <para><b>Why this exists.</b> <see cref="MemoryManager.CreateSceneScope"/>
    /// creates a scope <i>and</i> attaches it to Unity's <c>sceneUnloaded</c> in one
    /// step, which assumes two things a lot of projects do not do: that scenes are how
    /// the project ends things, and that the scope can be created after the scene is
    /// known. Neither holds for an Addressables scene load (the handle comes back
    /// later), a bespoke flow manager, an additive UI stack, or a match that ends
    /// without a scene change.</para>
    ///
    /// <para>The alternative — telling those projects to reorganise around scenes —
    /// is not a real option, and a lifetime the toolkit cannot express is a lifetime
    /// someone will hand-roll next to it. Two systems that disagree about ownership
    /// leak worse than one that never existed.</para>
    /// </summary>
    public static class MemoryScopeExtensions
    {
        /// <summary>
        /// Disposes <paramref name="scope"/> when <paramref name="host"/> is
        /// destroyed. Use the object whose destruction genuinely marks the end of the
        /// lifetime — a level root, a flow manager's context object, a DI container's
        /// GameObject.
        /// </summary>
        public static MemoryScope AttachTo(this MemoryScope scope, GameObject host)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (scope.IsDisposed) throw new ObjectDisposedException($"MemoryScope '{scope.Name}'");

            // One anchor per host, so attaching two scopes to the same object is a
            // caller error rather than a silent overwrite of the first scope's
            // disposal — losing that would leak the whole scope with no symptom.
            if (host.TryGetComponent(out MemoryScopeAnchor existing))
            {
                if (existing.Scope != null && existing.Scope != scope)
                {
                    throw new InvalidOperationException(
                        $"'{host.name}' is already the anchor for scope '{existing.Scope.Name}'. " +
                        "Anchor one scope per object, or make the second a child scope.");
                }

                existing.Scope = scope;
                return scope;
            }

            host.AddComponent<MemoryScopeAnchor>().Scope = scope;
            return scope;
        }

        /// <summary>
        /// Disposes <paramref name="scope"/> when <paramref name="scene"/> unloads.
        ///
        /// <para>Separate from <see cref="MemoryManager.CreateSceneScope"/> because
        /// the order is often reversed in practice: with Addressables you create the
        /// scope, start the load, and only then hold a <see cref="Scene"/> to attach
        /// to.</para>
        /// </summary>
        public static MemoryScope AttachTo(this MemoryScope scope, Scene scene)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (scope.IsDisposed) throw new ObjectDisposedException($"MemoryScope '{scope.Name}'");
            if (!scene.IsValid())
                throw new ArgumentException("Scene is not valid — attach after the load completes.", nameof(scene));

            void OnSceneUnloaded(Scene unloaded)
            {
                if (unloaded == scene) scope.Dispose();
            }

            SceneManager.sceneUnloaded += OnSceneUnloaded;
            scope.OnDisposed(() => SceneManager.sceneUnloaded -= OnSceneUnloaded);
            return scope;
        }

        /// <summary>
        /// Disposes <paramref name="scope"/> when <paramref name="trigger"/> fires,
        /// and unsubscribes on disposal either way. For lifetimes owned by an event
        /// the project already has — a flow manager's <c>SessionEnded</c>, a
        /// container's teardown callback.
        /// </summary>
        /// <example>
        /// <code>
        /// scope.DisposeWhen(
        ///     handler =&gt; flow.SessionEnded += handler,
        ///     handler =&gt; flow.SessionEnded -= handler);
        /// </code>
        /// </example>
        public static MemoryScope DisposeWhen(this MemoryScope scope, Action<Action> subscribe, Action<Action> unsubscribe)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (subscribe == null) throw new ArgumentNullException(nameof(subscribe));
            if (unsubscribe == null) throw new ArgumentNullException(nameof(unsubscribe));
            if (scope.IsDisposed) throw new ObjectDisposedException($"MemoryScope '{scope.Name}'");

            Action handler = null;
            handler = () => scope.Dispose();

            subscribe(handler);

            // Unsubscribing on disposal is the whole point: a scope disposed early
            // (manually, or by a parent) must not leave a delegate holding it alive
            // on an event that outlives it. That is the leak this method is most
            // likely to be used to fix.
            scope.OnDisposed(() => unsubscribe(handler));
            return scope;
        }
    }
}
