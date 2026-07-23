using System;
using MemoryToolkit.Migration;
using MemoryToolkit.Pooling;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    /// <summary>
    /// Covers Observe mode: the pre-migration measurement pass. The load-bearing
    /// property is that it changes nothing — a project must be able to ship it to a
    /// playtest — while still producing the one number a codebase with no pool
    /// cannot otherwise produce, the peak concurrent live count.
    /// </summary>
    public class PoolShadowTests
    {
        private GameObject _prefab;

        [SetUp]
        public void SetUp()
        {
            _prefab = new GameObject("ShadowPrefab");
            PoolBridge.ScopeResolver = null;
            PoolBridge.UnknownInstances = UnknownInstancePolicy.Ignore;
            PoolBridge.ResetDiagnostics();
            PoolShadow.Reset();
            PoolBridge.Mode = PoolBridgeMode.Observe;
        }

        [TearDown]
        public void TearDown()
        {
            PoolBridge.Mode = PoolBridgeMode.Active;
            PoolBridge.UnknownInstances = UnknownInstancePolicy.LogAndDestroy;
            PoolBridge.ResetDiagnostics();
            PoolShadow.Reset();
            MemoryManager.Permanent.Dispose();
            Object.DestroyImmediate(_prefab);
        }

        [Test]
        public void ObserveMode_CreatesNoPool_AndRecyclesNothing()
        {
            GameObject first = PoolBridge.Get(_prefab);
            Assert.That(PoolBridge.Return(first), Is.True);
            GameObject second = PoolBridge.Get(_prefab);

            Assert.That(second, Is.Not.SameAs(first),
                "Observe mode must instantiate exactly as the un-pooled code did; recycling would change behaviour");
            Assert.That(MemoryManager.Permanent.TryGetPool(_prefab, out GameObjectPool _), Is.False,
                "no pool may be created while only measuring");

            PoolBridge.Return(second);
        }

        [Test]
        public void ObserveMode_LeavesNoPooledInstanceComponentBehind()
        {
            GameObject instance = PoolBridge.Get(_prefab);

            Assert.That(instance.TryGetComponent(out PooledInstance _), Is.False);
            Assert.That(instance.TryGetComponent(out ShadowInstance _), Is.True,
                "attribution has to travel on the instance, not in a lookup table");

            PoolBridge.Return(instance);
        }

        [Test]
        public void PeakConcurrent_IsTheHighWaterMark_NotTheCurrentCount()
        {
            GameObject a = PoolBridge.Get(_prefab);
            GameObject b = PoolBridge.Get(_prefab);
            GameObject c = PoolBridge.Get(_prefab);
            PoolBridge.Return(a);
            PoolBridge.Return(b);

            ShadowEntry entry = PoolShadow.Entries[0];
            Assert.That(entry.PeakConcurrent, Is.EqualTo(3), "the peak is what sizes a pool");
            Assert.That(entry.Live, Is.EqualTo(1), "the instantaneous count is not the peak");
            Assert.That(entry.Gets, Is.EqualTo(3));
            Assert.That(entry.Returns, Is.EqualTo(2));

            PoolBridge.Return(c);
        }

        [Test]
        public void UnreturnedInstances_AreVisible_BecauseTheyWouldGrowThePool()
        {
            PoolBridge.Get(_prefab);
            GameObject returned = PoolBridge.Get(_prefab);
            PoolBridge.Return(returned);

            ShadowEntry entry = PoolShadow.Entries[0];
            Assert.That(entry.Gets - entry.Returns, Is.EqualTo(1));
            Assert.That(PoolShadow.Report(), Does.Contain("ShadowPrefab"));
        }

        [Test]
        public void ReturnOfAForeignInstance_IsUnattributed_AndSaysTheProjectionIsAFloor()
        {
            var foreign = new GameObject("NotOurs");

            Assert.That(PoolBridge.Return(foreign), Is.False);
            Assert.That(PoolShadow.UnattributedReturnCount, Is.EqualTo(1));
            Assert.That(PoolBridge.UnknownInstanceCount, Is.EqualTo(1),
                "the escape metric has to stay meaningful in both modes");
            // The report must surface this even though no get was ever observed:
            // "returns routed, gets not" is a real misconfiguration, and the warning
            // is the whole diagnosis for it.
            Assert.That(PoolShadow.Entries, Is.Empty);
            Assert.That(PoolShadow.Report(), Does.Contain("unattributed"));
        }

        [Test]
        public void DoubleReturn_OfAnAlreadyDestroyedInstance_IsSafeAndDoesNotDoubleCount()
        {
            GameObject instance = PoolBridge.Get(_prefab);

            Assert.That(PoolBridge.Return(instance), Is.True);

            // Edit mode destroys immediately, so the second return arrives holding a
            // destroyed object. It must not throw, and it must not be counted as a
            // second saving. Attribution is impossible here by construction — the
            // same-frame case, where Unity's deferred Destroy leaves the marker
            // readable, is covered in the play-mode tests.
            Assert.That(PoolBridge.Return(instance), Is.False, "a repeat return must be a safe no-op");

            Assert.That(PoolShadow.Entries[0].Returns, Is.EqualTo(1), "a double return must not double-count the saving");
            Assert.That(PoolShadow.Entries[0].Live, Is.Zero);
        }

        [Test]
        public void SwitchingToActiveMidSession_StillReleasesPooledInstancesProperly()
        {
            PoolBridge.Mode = PoolBridgeMode.Active;
            GameObject pooled = PoolBridge.Get(_prefab);

            // The realistic ordering mistake: flip back to Observe while instances a
            // pool still owns are in flight.
            PoolBridge.Mode = PoolBridgeMode.Observe;

            Assert.That(PoolBridge.Return(pooled), Is.True);
            Assert.That(PoolBridge.IsPooled(pooled), Is.True,
                "a pooled instance must go back to its pool, not be destroyed under it");
            Assert.That(PoolShadow.UnattributedReturnCount, Is.Zero);
        }

        [Test]
        public void GetTyped_ThrowsOnAMissingComponent_AndDoesNotLeaveTheInstanceLive()
        {
            Assert.Throws<InvalidOperationException>(() => PoolBridge.Get<Rigidbody>(_prefab));
            Assert.That(PoolShadow.Entries[0].Live, Is.Zero,
                "the rejected instance must not sit in the live count and inflate the peak");
        }

        [Test]
        public void CollectStats_ReportsShadowRows_SoTheTimelineDrawsThemLikePools()
        {
            GameObject a = PoolBridge.Get(_prefab);
            GameObject b = PoolBridge.Get(_prefab);

            var stats = new System.Collections.Generic.List<MemoryManager.PoolStat>();
            PoolShadow.CollectStats(stats);

            Assert.That(stats, Has.Count.EqualTo(1));
            Assert.That(stats[0].PrefabName, Is.EqualTo("ShadowPrefab"));
            Assert.That(stats[0].CountActive, Is.EqualTo(2));
            Assert.That(stats[0].CountInactive, Is.Zero, "nothing is ever retained while only measuring");
            Assert.That(stats[0].WasWarmedUp, Is.False);

            PoolBridge.Return(a);
            PoolBridge.Return(b);
        }

        [Test]
        public void ReportJson_IsParseableAndCarriesTheWarmupCount()
        {
            GameObject a = PoolBridge.Get(_prefab);
            PoolBridge.Get(_prefab);
            PoolBridge.Return(a);

            string json = PoolShadow.ReportJson();

            Assert.That(json, Does.Contain("\"schemaVersion\":1"));
            Assert.That(json, Does.Contain("\"prefab\":\"ShadowPrefab\""));
            Assert.That(json, Does.Contain("\"peakConcurrent\":2"));
            Assert.That(json, Does.Contain("\"unreturned\":1"));
        }

        [Test]
        public void Reset_ClearsMeasurementsBetweenRuns()
        {
            PoolBridge.Return(PoolBridge.Get(_prefab));
            PoolShadow.Reset();

            Assert.That(PoolShadow.Entries, Is.Empty);
            Assert.That(PoolShadow.UnattributedReturnCount, Is.Zero);
            Assert.That(PoolShadow.Report(), Does.Contain("no gets observed"));
        }
    }
}
