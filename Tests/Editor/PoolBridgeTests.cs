using System;
using MemoryToolkit.Migration;
using MemoryToolkit.Pooling;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    /// <summary>
    /// Covers the brownfield migration surface: a project keeps its existing
    /// call sites and re-points its few extension methods at the bridge.
    /// </summary>
    public class PoolBridgeTests
    {
        private GameObject _prefab;

        [SetUp]
        public void SetUp()
        {
            _prefab = new GameObject("BridgePrefab");
            PoolBridge.ScopeResolver = null;
            PoolBridge.UnknownInstances = UnknownInstancePolicy.LogAndDestroy;
            PoolBridge.ResetDiagnostics();
        }

        [TearDown]
        public void TearDown()
        {
            PoolBridge.ScopeResolver = null;
            PoolBridge.UnknownInstances = UnknownInstancePolicy.LogAndDestroy;
            PoolBridge.ResetDiagnostics();
            MemoryManager.Permanent.Dispose();
            Object.DestroyImmediate(_prefab);
        }

        [Test]
        public void Get_And_Return_RoundTripThroughAScopeOwnedPool()
        {
            GameObject first = PoolBridge.Get(_prefab);
            Assert.That(first.activeSelf, Is.True);
            Assert.That(PoolBridge.IsPooled(first), Is.False);

            Assert.That(PoolBridge.Return(first), Is.True);
            Assert.That(PoolBridge.IsPooled(first), Is.True);

            GameObject second = PoolBridge.Get(_prefab);
            Assert.That(second, Is.SameAs(first), "the instance should be recycled, not re-instantiated");
            Assert.That(PoolBridge.GetCount, Is.EqualTo(2));
            Assert.That(PoolBridge.ReturnCount, Is.EqualTo(1));
        }

        [Test]
        public void Return_IsIdempotent_SoCallSiteGuardsCanBeDeleted()
        {
            GameObject instance = PoolBridge.Get(_prefab);

            Assert.That(PoolBridge.Return(instance), Is.True);
            Assert.That(PoolBridge.Return(instance), Is.True, "a repeat return must be a safe no-op");

            // The instance must not be double-pushed onto the free list.
            MemoryManager.Permanent.TryGetPool(_prefab, out GameObjectPool pool);
            Assert.That(pool.CountInactive, Is.EqualTo(1));
            Assert.That(pool.DoubleReleaseCount, Is.EqualTo(1));
        }

        [Test]
        public void Return_OfAnInstanceFromAnotherRegistry_IsCountedAndPolicyApplies()
        {
            var foreign = new GameObject("NotOurs");
            PoolBridge.UnknownInstances = UnknownInstancePolicy.Ignore;

            Assert.That(PoolBridge.Return(foreign), Is.False);
            Assert.That(PoolBridge.UnknownInstanceCount, Is.EqualTo(1));
            Assert.That(foreign != null, "Ignore policy must leave the other registry's instance alone");

            Object.DestroyImmediate(foreign);
        }

        [Test]
        public void Return_OfAnUnknownInstance_ThrowsUnderStrictPolicy()
        {
            var foreign = new GameObject("NotOurs");
            PoolBridge.UnknownInstances = UnknownInstancePolicy.Throw;

            Assert.Throws<InvalidOperationException>(() => PoolBridge.Return(foreign));

            Object.DestroyImmediate(foreign);
        }

        [Test]
        public void Return_OfADestroyedInstance_IsFalse_NotAnException()
        {
            // The `?.` hazard: a destroyed Unity object is not reference-null, so a
            // bridge written with `?.` would proceed into the release path here.
            GameObject instance = PoolBridge.Get(_prefab);
            Object.DestroyImmediate(instance);

            Assert.That(PoolBridge.Return(instance), Is.False);
            Assert.That(PoolBridge.UnknownInstanceCount, Is.Zero, "a destroyed instance is not an unknown instance");
        }

        [Test]
        public void ScopeResolver_ChoosesTheOwningScope()
        {
            MemoryScope battle = MemoryManager.CreateScope("Battle");
            PoolBridge.ScopeResolver = _ => battle;

            GameObject instance = PoolBridge.Get(_prefab);

            Assert.That(battle.TryGetPool(_prefab, out _), Is.True);
            Assert.That(MemoryManager.Permanent.TryGetPool(_prefab, out _), Is.False,
                "the pool must belong to the resolved scope, not the default");

            battle.Dispose();
            Assert.That(instance == null, "disposing the owning scope must destroy its instances");
        }

        [Test]
        public void ScopeResolver_ReturningADisposedScope_FallsBackToPermanent()
        {
            MemoryScope dead = MemoryManager.CreateScope("Dead");
            dead.Dispose();
            PoolBridge.ScopeResolver = _ => dead;

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("disposed scope"));
            GameObject instance = PoolBridge.Get(_prefab);

            Assert.That(instance, Is.Not.Null);
            Assert.That(MemoryManager.Permanent.TryGetPool(_prefab, out _), Is.True);
        }

        [Test]
        public void LazyPoolCount_DistinguishesWarmedPoolsFromCallSiteCreatedOnes()
        {
            PoolBridge.Get(_prefab);
            Assert.That(PoolBridge.LazyPoolCount, Is.EqualTo(1));

            var warmed = new GameObject("WarmedPrefab");
            PoolBridge.ResetDiagnostics();
            PoolBridge.Warmup(warmed, 4);
            PoolBridge.Get(warmed);

            Assert.That(PoolBridge.LazyPoolCount, Is.Zero, "a warmed pool already exists when the first Get arrives");
            MemoryManager.Permanent.TryGetPool(warmed, out GameObjectPool pool);
            Assert.That(pool.WasWarmedUp, Is.True);

            Object.DestroyImmediate(warmed);
        }

        [Test]
        public void GetTyped_ResolvesComponentWithoutCallSiteGetComponent()
        {
            _prefab.AddComponent<BoxCollider>();

            BoxCollider collider = PoolBridge.Get<BoxCollider>(_prefab);

            Assert.That(collider, Is.Not.Null);
            Assert.That(PoolBridge.Return(collider.gameObject), Is.True);
        }
    }
}
