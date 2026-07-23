#if MEMORYTOOLKIT_ZENJECT
using System;
using Zenject;

namespace MemoryToolkit.Integrations
{
    /// <summary>
    /// Binds a <see cref="MemoryScope"/> to a Zenject container's lifetime.
    ///
    /// <para><b>Why adapt instead of add.</b> A project using Zenject already
    /// expresses lifetime — that is what a Context is. Adding
    /// <see cref="MemoryScope"/> alongside it creates a second ownership system that
    /// can disagree with the first: a context torn down while its memory scope lives
    /// leaks everything the scope owns, and a scope disposed first leaves resolved
    /// objects holding released pools. The scope is therefore a dependent of the
    /// container's lifetime, not a peer of it.</para>
    ///
    /// <code>
    /// public sealed class LevelInstaller : MonoInstaller
    /// {
    ///     public override void InstallBindings() =&gt; Container.BindMemoryScope("Level");
    /// }
    ///
    /// // ...anywhere downstream:
    /// [Inject] private MemoryScope _scope;
    /// </code>
    ///
    /// <para>Zenject disposes bindings that implement <see cref="IDisposable"/> only
    /// when they are bound into the disposal pipeline, which is what
    /// <c>BindMemoryScope</c> does — binding the scope for injection alone would
    /// resolve fine and never be torn down.</para>
    /// </summary>
    public static class ZenjectMemoryScopeExtensions
    {
        /// <summary>
        /// Creates a scope owned by this container and binds it for injection. The
        /// container disposes it when the context is destroyed.
        /// </summary>
        /// <param name="parent">
        /// Parent for pool lookup fallback. Defaults to
        /// <see cref="MemoryManager.Permanent"/>, so a prefab already pooled
        /// permanently is not duplicated per context.
        /// </param>
        public static MemoryScope BindMemoryScope(this DiContainer container, string name, MemoryScope parent = null)
        {
            if (container == null) throw new ArgumentNullException(nameof(container));
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Scope name is required.", nameof(name));

            return container.BindMemoryScope(MemoryManager.CreateScope(name, parent));
        }

        /// <summary>
        /// Gives an existing scope to this container's lifetime — for a scope created
        /// during a load, before the context existed, that should still die with it.
        /// </summary>
        public static MemoryScope BindMemoryScope(this DiContainer container, MemoryScope scope)
        {
            if (container == null) throw new ArgumentNullException(nameof(container));
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (scope.IsDisposed) throw new ObjectDisposedException($"MemoryScope '{scope.Name}'");

            // Both bindings are required and they are not the same thing: the first
            // makes the scope injectable, the second puts it in the container's
            // disposal pipeline. Binding only the first resolves perfectly and leaks
            // the entire scope — which is the failure this adapter exists to prevent,
            // so it is not left to the caller to remember.
            container.Bind<MemoryScope>().FromInstance(scope).AsSingle();
            container.BindInterfacesTo<MemoryScopeDisposable>()
                .AsSingle()
                .WithArguments(scope)
                .NonLazy();

            return scope;
        }

        /// <summary>
        /// Adapts <see cref="MemoryScope"/> into Zenject's disposal pipeline.
        ///
        /// <para>A wrapper rather than binding the scope's own <see cref="IDisposable"/>
        /// because <c>BindInterfacesTo&lt;MemoryScope&gt;</c> would also bind every
        /// other interface it implements, and because the scope must stay resolvable
        /// as itself.</para>
        /// </summary>
        private sealed class MemoryScopeDisposable : IDisposable
        {
            private readonly MemoryScope _scope;

            public MemoryScopeDisposable(MemoryScope scope) => _scope = scope;

            // Idempotent: a scope disposed earlier by a flow manager or a parent
            // scope makes this a no-op rather than a throw during shutdown.
            public void Dispose() => _scope.Dispose();
        }
    }
}
#endif
