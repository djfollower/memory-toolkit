using System;
using System.Collections.Generic;
using MemoryToolkit;
using MemoryToolkit.Pooling;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    public class MemoryScopeTests
    {
        private GameObject _prefab;

        [SetUp]
        public void SetUp() => _prefab = new GameObject("ScopeTestPrefab");

        [TearDown]
        public void TearDown()
        {
            MemoryManager.Shutdown();
            Object.DestroyImmediate(_prefab);
        }

        [Test]
        public void GetPool_CreatesPoolOwnedByScope_DisposedWithIt()
        {
            MemoryScope scope = MemoryManager.CreateScope("Level");
            GameObjectPool pool = scope.GetPool(_prefab);
            pool.Warmup(3);

            scope.Dispose();

            Assert.That(scope.IsDisposed, Is.True);
            Assert.Throws<ObjectDisposedException>(() => pool.Get());
        }

        [Test]
        public void GetPool_FallsBackToParentChain_InsteadOfDuplicating()
        {
            GameObjectPool permanentPool = MemoryManager.GetPool(_prefab);
            MemoryScope scene = MemoryManager.CreateScope("Scene");

            Assert.That(scene.GetPool(_prefab), Is.SameAs(permanentPool),
                "a prefab pooled permanently must not be duplicated per scene");
        }

        [Test]
        public void GetPool_InChildScope_DoesNotLeakIntoParent()
        {
            MemoryScope scene = MemoryManager.CreateScope("Scene");
            GameObjectPool scenePool = scene.GetPool(_prefab);
            scenePool.Warmup(2);
            scene.Dispose();

            // After the scene scope dies, the permanent scope must build a
            // fresh pool, not resurrect the disposed one.
            GameObjectPool permanentPool = MemoryManager.GetPool(_prefab);
            Assert.That(permanentPool, Is.Not.SameAs(scenePool));
            Assert.That(permanentPool.CountInactive, Is.EqualTo(0));
        }

        [Test]
        public void Register_DisposesOwnedDisposables_InReverseOrder()
        {
            MemoryScope scope = MemoryManager.CreateScope("Match");
            var order = new List<string>();
            scope.Register(new TrackingDisposable("first", order));
            scope.Register(new TrackingDisposable("second", order));

            scope.Dispose();

            Assert.That(order, Is.EqualTo(new[] { "second", "first" }));
        }

        [Test]
        public void CreateAllocator_FreesNativeBlock_OnScopeDispose()
        {
            MemoryScope scope = MemoryManager.CreateScope("Level");
            var arena = scope.CreateAllocator(256);
            arena.Allocate<int>(4);

            scope.Dispose();

            Assert.Throws<ObjectDisposedException>(() => arena.Allocate<int>(1));
        }

        [Test]
        public void Dispose_IsIdempotent()
        {
            MemoryScope scope = MemoryManager.CreateScope("Level");
            scope.GetPool(_prefab).Warmup(1);

            scope.Dispose();
            Assert.DoesNotThrow(scope.Dispose);
        }

        [Test]
        public void DisposedScope_ThrowsOnUse()
        {
            MemoryScope scope = MemoryManager.CreateScope("Level");
            scope.Dispose();

            Assert.Throws<ObjectDisposedException>(() => scope.GetPool(_prefab));
            Assert.Throws<ObjectDisposedException>(() => scope.Register(new TrackingDisposable("x", new List<string>())));
        }

        [Test]
        public void GetPoolStats_ReportsScopeNames_AndDropsDisposedScopes()
        {
            var otherPrefab = new GameObject("OtherPrefab");
            try
            {
                MemoryManager.GetPool(_prefab).Warmup(1);
                MemoryScope scene = MemoryManager.CreateScope("Scene A");
                scene.GetPool(otherPrefab).Warmup(2);

                var stats = new List<MemoryManager.PoolStat>();
                MemoryManager.GetPoolStats(stats);
                Assert.That(stats.Count, Is.EqualTo(2));
                Assert.That(stats[0].ScopeName, Is.EqualTo("Permanent"));
                Assert.That(stats[1].ScopeName, Is.EqualTo("Scene A"));

                scene.Dispose();
                MemoryManager.GetPoolStats(stats);
                Assert.That(stats.Count, Is.EqualTo(1), "disposed scopes must vanish from stats");
            }
            finally
            {
                Object.DestroyImmediate(otherPrefab);
            }
        }

        [Test]
        public void Pin_HoldsStrongReference_UntilScopeDisposed()
        {
            var config = ScriptableObject.CreateInstance<ScriptableObject>();
            try
            {
                MemoryScope permanent = MemoryManager.Permanent;
                MemoryScope login = MemoryManager.CreateScope("Login");

                // Loaded "in the login scene", owned by Permanent.
                Assert.That(permanent.Pin(config), Is.SameAs(config));
                permanent.Pin(config); // idempotent, no duplicate entry
                Assert.That(permanent.PinnedAssets.Count, Is.EqualTo(1));

                // The momentary scope dying must not touch the pin.
                login.Dispose();
                Assert.That(permanent.PinnedAssets.Count, Is.EqualTo(1));
                Assert.That(permanent.PinnedAssets[0], Is.SameAs(config));

                permanent.Dispose();
                Assert.That(permanent.PinnedAssets.Count, Is.EqualTo(0), "dispose drops pin references");
                Assert.That(config != null, Is.True, "pin release must not destroy the asset itself");
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void Pin_Throws_OnNullOrDisposedScope()
        {
            MemoryScope scope = MemoryManager.CreateScope("Level");
            Assert.Throws<ArgumentNullException>(() => scope.Pin<ScriptableObject>(null));

            scope.Dispose();
            var asset = ScriptableObject.CreateInstance<ScriptableObject>();
            try
            {
                Assert.Throws<ObjectDisposedException>(() => scope.Pin(asset));
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void CreateAllocator_IsListedForInspection()
        {
            MemoryScope scope = MemoryManager.CreateScope("Level");
            var arena = scope.CreateAllocator(256);

            Assert.That(scope.Allocators.Count, Is.EqualTo(1));
            Assert.That(scope.Allocators[0], Is.SameAs(arena));

            scope.Dispose();
            Assert.That(scope.Allocators.Count, Is.EqualTo(0));
        }

        [Test]
        public void Shutdown_DisposesAllScopes()
        {
            MemoryScope scene = MemoryManager.CreateScope("Scene");
            MemoryScope permanent = MemoryManager.Permanent;

            MemoryManager.Shutdown();

            Assert.That(scene.IsDisposed, Is.True);
            Assert.That(permanent.IsDisposed, Is.True);
        }

        private sealed class TrackingDisposable : IDisposable
        {
            private readonly string _name;
            private readonly List<string> _order;

            public TrackingDisposable(string name, List<string> order)
            {
                _name = name;
                _order = order;
            }

            public void Dispose() => _order.Add(_name);
        }
    }
}
