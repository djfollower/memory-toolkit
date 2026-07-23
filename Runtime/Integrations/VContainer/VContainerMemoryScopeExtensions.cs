#if MEMORYTOOLKIT_VCONTAINER
using System;
using VContainer;
using VContainer.Unity;

namespace MemoryToolkit.Integrations
{
    /// <summary>
    /// Binds a <see cref="MemoryScope"/> to a VContainer <c>LifetimeScope</c>.
    ///
    /// <para><b>Why adapt instead of add.</b> A project using VContainer already
    /// expresses lifetime — that is what a LifetimeScope is. Asking it to adopt
    /// <see cref="MemoryScope"/> <i>as well</i> creates a second ownership system
    /// alongside the first, and the two can disagree: a container torn down while its
    /// memory scope lives leaks everything the scope owns, and a scope disposed first
    /// leaves resolved objects holding released pools. When that happens the blame
    /// lands on the package that arrived second.</para>
    ///
    /// <para>So the memory scope is not a peer of the container's lifetime, it is a
    /// dependent of it: registered as an instance the container owns, disposed by the
    /// container, in the container's order. There is exactly one lifetime, and it is
    /// the one the project already had.</para>
    ///
    /// <code>
    /// public sealed class LevelScope : LifetimeScope
    /// {
    ///     protected override void Configure(IContainerBuilder builder)
    ///     {
    ///         builder.RegisterMemoryScope("Level");
    ///         builder.Register&lt;EnemySpawner&gt;(Lifetime.Scoped);
    ///     }
    /// }
    ///
    /// // ...anywhere downstream:
    /// public EnemySpawner(MemoryScope scope) =&gt; _pool = scope.GetPool(_prefab);
    /// </code>
    /// </summary>
    public static class VContainerMemoryScopeExtensions
    {
        /// <summary>
        /// Creates a scope owned by this container and registers it for injection.
        /// The container disposes it when the LifetimeScope is destroyed.
        /// </summary>
        /// <param name="parent">
        /// Parent for pool lookup fallback. Defaults to
        /// <see cref="MemoryManager.Permanent"/>, so a prefab already pooled
        /// permanently is not duplicated per container.
        /// </param>
        public static RegistrationBuilder RegisterMemoryScope(
            this IContainerBuilder builder, string name, MemoryScope parent = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Scope name is required.", nameof(name));

            MemoryScope scope = MemoryManager.CreateScope(name, parent);

            // RegisterInstance does NOT dispose the instance — VContainer only owns
            // what it creates. Registering the scope and assuming disposal is the
            // obvious mistake here, and it leaks the entire scope silently, so the
            // teardown is wired explicitly rather than left to a convention.
            RegistrationBuilder registration = builder.RegisterInstance(scope);
            builder.RegisterDisposeCallback(_ => scope.Dispose());
            return registration;
        }

        /// <summary>
        /// Gives an existing scope to this container's lifetime — for a scope created
        /// earlier (during a load, before the container was built) that should still
        /// die with it.
        /// </summary>
        public static RegistrationBuilder RegisterMemoryScope(this IContainerBuilder builder, MemoryScope scope)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (scope.IsDisposed) throw new ObjectDisposedException($"MemoryScope '{scope.Name}'");

            RegistrationBuilder registration = builder.RegisterInstance(scope);

            // The container's disposal is the trigger. Disposing the scope earlier —
            // manually, or via a parent scope — is safe: MemoryScope.Dispose is
            // idempotent, so a double dispose is a no-op either way round.
            builder.RegisterDisposeCallback(_ => scope.Dispose());
            return registration;
        }
    }
}
#endif
