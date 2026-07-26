using System.Collections.Generic;
using MemoryToolkit.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MemoryToolkit.Tests
{
    /// <summary>
    /// Covers breadcrumbs: the bounded key set a crash reporter carries, and the
    /// no-op default that keeps a game that never opts in from paying for any of it.
    /// </summary>
    public class MemoryBreadcrumbsTests
    {
        private GameObject _prefab;
        private RecordingSink _sink;

        [SetUp]
        public void SetUp()
        {
            _prefab = new GameObject("CrumbPrefab");
            _sink = new RecordingSink();
        }

        [TearDown]
        public void TearDown()
        {
            MemoryBreadcrumbs.Sink = null; // restores the no-op
            MemoryManager.Permanent.Dispose();
            Object.DestroyImmediate(_prefab);
        }

        [Test]
        public void WithoutASink_CaptureDoesNothing()
        {
            Assert.That(MemoryBreadcrumbs.HasSink, Is.False);
            Assert.DoesNotThrow(MemoryBreadcrumbs.Capture);
        }

        [Test]
        public void SettingNullSink_RestoresTheNoOp_RatherThanThrowingLater()
        {
            MemoryBreadcrumbs.Sink = _sink;
            Assert.That(MemoryBreadcrumbs.HasSink, Is.True);

            MemoryBreadcrumbs.Sink = null;
            Assert.That(MemoryBreadcrumbs.HasSink, Is.False,
                "a null sink must not leave a null to be dereferenced from the low-memory handler");
        }

        [Test]
        public void Capture_EmitsTheBudgetedHeadlineKeys()
        {
            MemoryBreadcrumbs.Sink = _sink;
            MemoryBreadcrumbs.Capture();

            Assert.That(_sink.Keys, Contains.Item("mtk_escapes"));
            Assert.That(_sink.Keys, Contains.Item("mtk_managed_mb"));
            Assert.That(_sink.Keys, Contains.Item("mtk_scopes"));
            Assert.That(_sink.Keys, Contains.Item("mtk_busiest_pools"));
        }

        [Test]
        public void TheKeySet_StaysUnderTheCrashReporterCap()
        {
            MemoryBreadcrumbs.Sink = _sink;
            MemoryBreadcrumbs.Capture();

            // Crashlytics allows 64 keys of 1 KB. The whole design is a fixed handful,
            // not a key per pool, so a payload can never be truncated into dropping
            // exactly the fields we added.
            Assert.That(_sink.Values.Count, Is.LessThan(16));
            foreach (KeyValuePair<string, string> pair in _sink.Values)
                Assert.That(pair.Value.Length, Is.LessThan(1024), $"key '{pair.Key}' exceeds the 1 KB cap");
        }

        [Test]
        public void BusiestPools_NamesTheActivePools()
        {
            GameObjectPoolWarmAndTake(2);
            MemoryBreadcrumbs.Sink = _sink;
            MemoryBreadcrumbs.Capture();

            Assert.That(_sink.Values["mtk_busiest_pools"], Does.Contain("CrumbPrefab"));
        }

        [Test]
        public void OnLowMemory_CountsAndCaptures()
        {
            MemoryBreadcrumbs.Sink = _sink;
            int before = MemoryBreadcrumbs.LowMemoryCount;

            MemoryBreadcrumbs.OnLowMemory();

            Assert.That(MemoryBreadcrumbs.LowMemoryCount, Is.EqualTo(before + 1));
            Assert.That(MemoryBreadcrumbs.LastLowMemoryTime, Is.GreaterThanOrEqualTo(0));
            Assert.That(_sink.Keys, Contains.Item("mtk_low_memory_count"));
        }

        private void GameObjectPoolWarmAndTake(int count)
        {
            Pooling.GameObjectPool pool = MemoryManager.Permanent.GetPool(_prefab);
            for (int i = 0; i < count; i++) pool.Get();
        }

        private sealed class RecordingSink : IBreadcrumbSink
        {
            public readonly List<string> Keys = new();
            public readonly Dictionary<string, string> Values = new();

            public void Set(string key, string value)
            {
                Keys.Add(key);
                Values[key] = value;
            }
        }
    }
}
