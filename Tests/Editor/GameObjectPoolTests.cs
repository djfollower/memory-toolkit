using MemoryToolkit.Pooling;
using NUnit.Framework;
using UnityEngine;

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
    }
}
