using System;
using MemoryToolkit.Pooling;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    public class GameObjectPoolTests
    {
        private GameObject _prefab;
        private GameObjectPool _pool;

        [SetUp]
        public void SetUp()
        {
            _prefab = new GameObject("TestPrefab");
            _pool = new GameObjectPool(_prefab, defaultCapacity: 4, maxSize: 8);
        }

        [TearDown]
        public void TearDown()
        {
            _pool.Dispose();
            Object.DestroyImmediate(_prefab);
        }

        [Test]
        public void Get_ReturnsActiveInstance_WithPooledHandle()
        {
            GameObject instance = _pool.Get(Vector3.one, Quaternion.identity);

            Assert.That(instance.activeSelf, Is.True);
            Assert.That(instance.transform.position, Is.EqualTo(Vector3.one));
            Assert.That(instance.GetComponent<PooledInstance>(), Is.Not.Null);
            Assert.That(_pool.CountActive, Is.EqualTo(1));
        }

        [Test]
        public void Release_ReturnsInstanceToPool_AndGetReusesIt()
        {
            GameObject first = _pool.Get();
            _pool.Release(first);

            Assert.That(_pool.CountInactive, Is.EqualTo(1));
            Assert.That(first.activeSelf, Is.False);

            GameObject second = _pool.Get();
            Assert.That(second, Is.SameAs(first), "pool should recycle, not instantiate");
            Assert.That(_pool.CountInactive, Is.EqualTo(0));
        }

        [Test]
        public void Warmup_PreallocatesRequestedCount_WithoutActiveInstances()
        {
            _pool.Warmup(5);

            Assert.That(_pool.CountInactive, Is.EqualTo(5));
            Assert.That(_pool.CountActive, Is.EqualTo(0));
        }

        [Test]
        public void Warmup_IsIdempotent_WhenAlreadyAtCount()
        {
            _pool.Warmup(3);
            _pool.Warmup(3);

            Assert.That(_pool.CountInactive, Is.EqualTo(3));
            Assert.That(_pool.CountAll, Is.EqualTo(3));
        }

        [Test]
        public void Trim_ReducesInactiveToKeepCount()
        {
            _pool.Warmup(6);
            _pool.Trim(2);

            Assert.That(_pool.CountInactive, Is.EqualTo(2));
        }

        [Test]
        public void Trim_ToZero_ClearsPool()
        {
            _pool.Warmup(4);
            _pool.Trim(0);

            Assert.That(_pool.CountInactive, Is.EqualTo(0));
        }

        [Test]
        public void Dispose_DestroysActiveInstances_NotJustPooledOnes()
        {
            GameObject active = _pool.Get();
            GameObject released = _pool.Get();
            _pool.Release(released);

            _pool.Dispose();

            // In edit mode destruction is immediate, so Unity's null-equality
            // reflects it right away.
            Assert.That(active == null, Is.True, "active instances are owned by the pool and must die with it");
            Assert.That(released == null, Is.True);

            // Recreate so TearDown's Dispose stays valid.
            _pool = new GameObjectPool(_prefab, defaultCapacity: 4, maxSize: 8);
        }

        [Test]
        public void PooledInstanceRelease_ReturnsToOwningPool_AndIsDoubleReleaseSafe()
        {
            GameObject instance = _pool.Get();
            var handle = instance.GetComponent<PooledInstance>();

            handle.Release();
            handle.Release(); // second call must be a no-op, not a pool corruption

            Assert.That(_pool.CountInactive, Is.EqualTo(1));
            Assert.That(_pool.CountActive, Is.EqualTo(0));
        }

        [Test]
        public void Release_OfAnInstanceFromAnotherPool_ThrowsNamingBothPrefabs()
        {
            // The characteristic failure of a migration running two registries:
            // silent unless someone checks, and it corrupts both pools' accounting.
            var otherPrefab = new GameObject("OtherPrefab");
            var otherPool = new GameObjectPool(otherPrefab, defaultCapacity: 2, maxSize: 4);
            try
            {
                GameObject foreign = otherPool.Get();

                var ex = Assert.Throws<InvalidOperationException>(() => _pool.Release(foreign));
                Assert.That(ex.Message, Does.Contain("OtherPrefab").And.Contain("TestPrefab"));
                Assert.That(otherPool.CountActive, Is.EqualTo(1), "the rejected release must not alter either pool");
                Assert.That(_pool.CountInactive, Is.Zero);
            }
            finally
            {
                otherPool.Dispose();
                Object.DestroyImmediate(otherPrefab);
            }
        }

        [Test]
        public void Release_OfAnInstanceNeverPooled_Throws()
        {
            var loose = new GameObject("Loose");
            Assert.Throws<InvalidOperationException>(() => _pool.Release(loose));
            Object.DestroyImmediate(loose);
        }

        [Test]
        public void Release_IsO1DoubleReleaseSafe_AndCounted()
        {
            GameObject instance = _pool.Get();

            _pool.Release(instance);
            _pool.Release(instance);
            _pool.Release(instance);

            Assert.That(_pool.CountInactive, Is.EqualTo(1), "repeats must not push the instance again");
            Assert.That(_pool.DoubleReleaseCount, Is.EqualTo(2));
        }

        [Test]
        public void WasWarmedUp_IsFalseForLazilyCreatedPools()
        {
            Assert.That(_pool.WasWarmedUp, Is.False);
            _pool.Get();
            Assert.That(_pool.WasWarmedUp, Is.False, "a Get must not count as declaring capacity");

            _pool.Warmup(2);
            Assert.That(_pool.WasWarmedUp, Is.True);
        }

        [Test]
        public void GetTyped_ReturnsComponent_AndServesRepeatCallsFromCache()
        {
            _prefab.AddComponent<BoxCollider>();
            var pool = new GameObjectPool(_prefab, defaultCapacity: 2, maxSize: 4);
            try
            {
                BoxCollider first = pool.Get<BoxCollider>();
                Assert.That(first, Is.Not.Null);
                Assert.That(first.gameObject.activeSelf, Is.True);

                pool.Release(first); // component overload, no .gameObject at the call site
                Assert.That(pool.CountInactive, Is.EqualTo(1));

                BoxCollider second = pool.Get<BoxCollider>();
                Assert.That(second, Is.SameAs(first), "same instance, and the cached component with it");
            }
            finally
            {
                pool.Dispose();
            }
        }

        [Test]
        public void GetTyped_Throws_WhenPrefabLacksComponent()
        {
            // A silent null here would be dereferenced frames later, far from
            // the actual mistake — so it fails at the call site instead.
            Assert.Throws<InvalidOperationException>(() => _pool.Get<BoxCollider>());
            Assert.That(_pool.CountActive, Is.EqualTo(0), "the failed Get must not leak an active instance");
        }

        [Test]
        public void Generation_Increments_OnEachReturnToPool()
        {
            GameObject instance = _pool.Get();
            var handle = instance.GetComponent<PooledInstance>();
            uint first = handle.Generation;

            _pool.Release(instance);
            Assert.That(handle.Generation, Is.Not.EqualTo(first));
        }

        [Test]
        public void PooledRef_GoesStale_WhenInstanceIsRecycled()
        {
            _prefab.AddComponent<BoxCollider>();
            var pool = new GameObjectPool(_prefab, defaultCapacity: 2, maxSize: 4);
            try
            {
                BoxCollider taken = pool.Get<BoxCollider>();
                PooledRef<BoxCollider> reference = PooledRef.To(taken);
                Assert.That(reference.IsAlive, Is.True);
                Assert.That(reference.TryGet(out BoxCollider live), Is.True);
                Assert.That(live, Is.SameAs(taken));

                pool.Release(taken);

                // The object is alive and non-null — a null check would pass —
                // but it is no longer the caller's to touch.
                Assert.That(taken != null, Is.True);
                Assert.That(reference.IsAlive, Is.False);
                Assert.That(reference.TryGet(out _), Is.False);

                // And it stays stale once handed to the next owner.
                BoxCollider reused = pool.Get<BoxCollider>();
                Assert.That(reused, Is.SameAs(taken));
                Assert.That(reference.IsAlive, Is.False);
            }
            finally
            {
                pool.Dispose();
            }
        }

        [Test]
        public void PooledRef_TreatsNonPooledComponent_AsAliveWhileNonNull()
        {
            // Call sites should not need to know whether their target is pooled.
            var loose = new GameObject("NotPooled");
            var collider = loose.AddComponent<BoxCollider>();
            PooledRef<BoxCollider> reference = PooledRef.To(collider);

            Assert.That(reference.IsAlive, Is.True);

            Object.DestroyImmediate(loose);
            Assert.That(reference.IsAlive, Is.False);
        }

        [Test]
        public void PooledRef_ToNull_IsNotAlive()
        {
            Assert.That(PooledRef.To<BoxCollider>(null).IsAlive, Is.False);
            Assert.That(default(PooledRef<BoxCollider>).IsAlive, Is.False);
        }
    }
}
