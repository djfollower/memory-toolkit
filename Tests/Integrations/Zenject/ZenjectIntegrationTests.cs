#if MEMORYTOOLKIT_ZENJECT
using MemoryToolkit.Integrations;
using MemoryToolkit.Pooling;
using NUnit.Framework;
using UnityEngine;
using Zenject;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    /// <summary>
    /// Proves the claim the adapter is built on: there is one lifetime, and it is the
    /// container's. Binding a scope for injection without binding it for disposal
    /// resolves perfectly and leaks the whole scope, so that is the case under test.
    ///
    /// <para>This assembly only compiles when Extenject is installed.</para>
    /// </summary>
    public class ZenjectIntegrationTests
    {
        private GameObject _prefab;

        [SetUp]
        public void SetUp() => _prefab = new GameObject("ZenjectPrefab");

        [TearDown]
        public void TearDown()
        {
            MemoryManager.Permanent.Dispose();
            Object.DestroyImmediate(_prefab);
        }

        /// <summary>
        /// A bare <see cref="DiContainer"/> has no <see cref="DisposableManager"/> —
        /// a real Context installs one, and it is what runs the disposal pipeline
        /// that <c>BindMemoryScope</c> binds into. Binding it here is what makes
        /// these tests a faithful stand-in for a Context rather than a weaker one.
        /// </summary>
        private static DiContainer NewContainer()
        {
            var container = new DiContainer();
            container.Bind<DisposableManager>().AsSingle();
            return container;
        }

        [Test]
        public void DisposingTheContainer_DisposesTheScope()
        {
            DiContainer container = NewContainer();
            MemoryScope scope = container.BindMemoryScope("Level");
            container.ResolveRoots();

            Assert.That(scope.IsDisposed, Is.False);

            container.Resolve<DisposableManager>().Dispose();

            Assert.That(scope.IsDisposed, Is.True,
                "binding a scope for injection without binding it for disposal resolves fine and leaks all of it");
        }

        [Test]
        public void TheScopeIsInjectable()
        {
            DiContainer container = NewContainer();
            container.BindMemoryScope("Level");
            container.Bind<Consumer>().AsSingle();
            container.ResolveRoots();

            Assert.That(container.Resolve<Consumer>().Scope.Name, Is.EqualTo("Level"));
        }

        [Test]
        public void PoolsOwnedByTheScope_AreFreedWithTheContainer()
        {
            DiContainer container = NewContainer();
            MemoryScope scope = container.BindMemoryScope("Level");
            container.ResolveRoots();

            scope.Warmup(_prefab, 4);
            Assert.That(scope.TryGetPool(_prefab, out GameObjectPool pool), Is.True);
            Assert.That(pool.CountInactive, Is.EqualTo(4));

            container.Resolve<DisposableManager>().Dispose();

            Assert.That(pool.CountInactive, Is.Zero, "the container's teardown must reach the scope's pools");
        }

        [Test]
        public void DisposingTheScopeFirst_IsSafe()
        {
            DiContainer container = NewContainer();
            MemoryScope scope = container.BindMemoryScope("Level");
            container.ResolveRoots();

            scope.Dispose();

            Assert.DoesNotThrow(() => container.Resolve<DisposableManager>().Dispose());
        }

        private sealed class Consumer
        {
            public readonly MemoryScope Scope;
            public Consumer(MemoryScope scope) => Scope = scope;
        }
    }
}
#endif
