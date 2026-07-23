#if MEMORYTOOLKIT_VCONTAINER
using MemoryToolkit.Integrations;
using MemoryToolkit.Pooling;
using NUnit.Framework;
using UnityEngine;
using VContainer;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    /// <summary>
    /// Proves the claim the adapter is built on: there is one lifetime, and it is the
    /// container's. Everything else about this integration is convenience; this is the
    /// part that, if wrong, leaks an entire scope with no symptom at the call site.
    ///
    /// <para>This assembly only compiles when VContainer is installed, which is why it
    /// is a separate asmdef with the same version define as the adapter.</para>
    /// </summary>
    public class VContainerIntegrationTests
    {
        private GameObject _prefab;

        [SetUp]
        public void SetUp() => _prefab = new GameObject("VContainerPrefab");

        [TearDown]
        public void TearDown()
        {
            MemoryManager.Permanent.Dispose();
            Object.DestroyImmediate(_prefab);
        }

        [Test]
        public void DisposingTheContainer_DisposesTheScope()
        {
            var builder = new ContainerBuilder();
            builder.RegisterMemoryScope("Level");
            IObjectResolver container = builder.Build();

            var scope = container.Resolve<MemoryScope>();
            Assert.That(scope.IsDisposed, Is.False);

            container.Dispose();

            Assert.That(scope.IsDisposed, Is.True,
                "RegisterInstance does not transfer ownership in VContainer; if this fails the scope leaks silently");
        }

        [Test]
        public void TheScopeIsInjectable_SoGameplayCodeNeverReachesForAGlobal()
        {
            var builder = new ContainerBuilder();
            builder.RegisterMemoryScope("Level");
            builder.Register<Consumer>(Lifetime.Scoped);

            using IObjectResolver container = builder.Build();
            var consumer = container.Resolve<Consumer>();

            Assert.That(consumer.Scope.Name, Is.EqualTo("Level"));
        }

        [Test]
        public void PoolsOwnedByTheScope_AreFreedWithTheContainer()
        {
            var builder = new ContainerBuilder();
            builder.RegisterMemoryScope("Level");
            IObjectResolver container = builder.Build();

            var scope = container.Resolve<MemoryScope>();
            scope.Warmup(_prefab, 4);
            Assert.That(scope.TryGetPool(_prefab, out GameObjectPool pool), Is.True);
            Assert.That(pool.CountInactive, Is.EqualTo(4));

            container.Dispose();

            Assert.That(pool.CountInactive, Is.Zero, "the container's teardown must reach the scope's pools");
        }

        [Test]
        public void AnExistingScopeCanBeHandedToTheContainer()
        {
            MemoryScope existing = MemoryManager.CreateScope("LoadedEarly");

            var builder = new ContainerBuilder();
            builder.RegisterMemoryScope(existing);
            IObjectResolver container = builder.Build();

            Assert.That(container.Resolve<MemoryScope>(), Is.SameAs(existing));

            container.Dispose();

            Assert.That(existing.IsDisposed, Is.True);
        }

        [Test]
        public void DisposingTheScopeFirst_IsSafe()
        {
            var builder = new ContainerBuilder();
            builder.RegisterMemoryScope("Level");
            IObjectResolver container = builder.Build();

            var scope = container.Resolve<MemoryScope>();
            scope.Dispose();

            // The realistic ordering accident: a flow manager tears the scope down
            // before the container. Dispose is idempotent, so this must be a no-op
            // rather than the second teardown throwing during shutdown.
            Assert.DoesNotThrow(() => container.Dispose());
        }

        private sealed class Consumer
        {
            public readonly MemoryScope Scope;
            public Consumer(MemoryScope scope) => Scope = scope;
        }
    }
}
#endif
